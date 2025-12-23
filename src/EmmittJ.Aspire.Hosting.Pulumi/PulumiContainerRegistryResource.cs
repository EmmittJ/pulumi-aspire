// Licensed under the Apache License, Version 2.0.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIRECOMPUTE003

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EmmittJ.Aspire.Hosting.Pulumi;

/// <summary>
/// Abstract base class for Pulumi-managed container registry resources.
/// </summary>
/// <remarks>
/// <para>
/// This resource manages a container registry (like Azure Container Registry or AWS ECR) through Pulumi.
/// It is provisioned as a separate Pulumi stack before the main environment, allowing images to be
/// pushed before deploying the compute resources.
/// </para>
/// <para>
/// Implements <see cref="IPulumiEnvironmentResource"/> to share naming conventions with environment resources.
/// The <see cref="IPulumiEnvironmentResource.ResourcePrefix"/> is computed as <c>{PulumiProjectName}-{EnvironmentName}</c>.
/// </para>
/// <para>
/// The registry is provisioned as part of the deployment pipeline with the following steps:
/// <list type="number">
/// <item>pulumi-deploy-registry: Provisions the registry using Pulumi</item>
/// <item>login-registry: Authenticates to the registry using the configured callback</item>
/// </list>
/// </para>
/// </remarks>
public abstract class PulumiContainerRegistryResource : Resource, IContainerRegistry, IPulumiEnvironmentResource
{
    private string? _resolvedName;
    private string? _resolvedEndpoint;

    /// <summary>
    /// Initializes a new instance of the <see cref="PulumiContainerRegistryResource"/> class.
    /// </summary>
    /// <param name="name">The name of the container registry resource.</param>
    protected PulumiContainerRegistryResource(string name)
        : base(name)
    {
        // Add pipeline step annotation for registry provisioning
        Annotations.Add(new PipelineStepAnnotation(CreatePipelineStepsAsync));
    }

    /// <summary>
    /// Gets or sets the Pulumi project name.
    /// All stacks are grouped under this project in the Pulumi console.
    /// </summary>
    public abstract string PulumiProjectName { get; set; }

    /// <summary>
    /// Gets the environment name (e.g., "dev", "staging", "prod").
    /// </summary>
    /// <remarks>
    /// Combined with <see cref="PulumiProjectName"/> to form the <see cref="ResourcePrefix"/>.
    /// </remarks>
    public abstract string EnvironmentName { get; }

    /// <summary>
    /// Gets the computed prefix for resource naming and stack identification.
    /// </summary>
    /// <remarks>
    /// This is computed as <c>{PulumiProjectName}-{EnvironmentName}</c>.
    /// </remarks>
    public string ResourcePrefix => $"{PulumiProjectName}-{EnvironmentName}";

    /// <summary>
    /// Gets or sets the callback for authenticating to the container registry.
    /// </summary>
    /// <remarks>
    /// This callback is invoked during the login-registry pipeline step after the registry is provisioned.
    /// Use <see cref="PulumiContainerRegistryHelpers"/> to create common login callbacks for Azure, AWS, and Docker.
    /// </remarks>
    public Func<PipelineStepContext, IContainerRegistry, Task>? LoginCallback { get; set; }

    /// <summary>
    /// Gets the resolved container registry name.
    /// Available after provisioning.
    /// </summary>
    public string? ResolvedName => _resolvedName;

    /// <summary>
    /// Gets the resolved container registry endpoint.
    /// Available after provisioning.
    /// </summary>
    public string? ResolvedEndpoint => _resolvedEndpoint;

    // IContainerRegistry implementation
    ReferenceExpression IContainerRegistry.Name => ReferenceExpression.Create($"{_resolvedName ?? Name}");
    ReferenceExpression IContainerRegistry.Endpoint => ReferenceExpression.Create($"{_resolvedEndpoint ?? $"{Name}.azurecr.io"}");

    /// <summary>
    /// Creates the container registry resources using Pulumi.
    /// </summary>
    /// <param name="context">The pipeline step context.</param>
    /// <returns>A task that returns the registry name and endpoint.</returns>
    protected abstract Task<(string Name, string Endpoint)> CreateRegistryAsync(PipelineStepContext context);

    /// <summary>
    /// Creates resources within the Pulumi program callback.
    /// Required by <see cref="IPulumiEnvironmentResource"/> but not used for registries
    /// (they use <see cref="CreateRegistryAsync"/> instead).
    /// </summary>
    Task IPulumiEnvironmentResource.CreateResourcesAsync(PulumiPublishingContext context)
    {
        // Registry resources are created via CreateRegistryAsync, not this method
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets the host address expression for an endpoint.
    /// Not applicable for container registries.
    /// </summary>
    ReferenceExpression IComputeEnvironmentResource.GetHostAddressExpression(EndpointReference endpointReference)
    {
        throw new NotSupportedException("Container registries do not provide host address expressions.");
    }

    /// <summary>
    /// Creates the pipeline steps for this registry.
    /// </summary>
    protected virtual Task<IEnumerable<PipelineStep>> CreatePipelineStepsAsync(PipelineStepFactoryContext factoryContext)
    {
        var steps = new List<PipelineStep>();

        // Deploy registry step
        var deployStep = new PipelineStep
        {
            Name = PulumiWellKnownPipelineSteps.DeployRegistry(Name),
            Description = $"Provisions container registry '{Name}' using Pulumi.",
            Action = DeployRegistryAsync,
            Resource = this,
            Tags = [WellKnownPipelineTags.ProvisionInfrastructure, PulumiWellKnownPipelineSteps.PulumiRegistryTag]
        };
        deployStep.DependsOn(WellKnownPipelineSteps.PublishPrereq);
        // Registry is required by Push, not Build - allows building images in parallel with registry provisioning
        deployStep.RequiredBy(WellKnownPipelineSteps.PushPrereq);
        steps.Add(deployStep);

        // Login step (if callback is configured)
        if (LoginCallback is not null)
        {
            var loginStep = new PipelineStep
            {
                Name = PulumiWellKnownPipelineSteps.LoginRegistry(Name),
                Description = $"Authenticates to container registry '{Name}'.",
                Action = LoginToRegistryAsync,
                Resource = this,
                Tags = [WellKnownPipelineTags.ProvisionInfrastructure, PulumiWellKnownPipelineSteps.PulumiRegistryTag],
                DependsOnSteps = [PulumiWellKnownPipelineSteps.DeployRegistry(Name)],
                // Login is required before push steps can run
                RequiredBySteps = [WellKnownPipelineSteps.PushPrereq]
            };
            steps.Add(loginStep);
        }

        return Task.FromResult<IEnumerable<PipelineStep>>(steps);
    }

    /// <summary>
    /// Deploys the container registry using Pulumi.
    /// </summary>
    private async Task DeployRegistryAsync(PipelineStepContext context)
    {
        var logger = context.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger<PulumiContainerRegistryResource>();

        var task = await context.ReportingStep.CreateTaskAsync(
            $"Provisioning container registry **{Name}**", context.CancellationToken);

        await using (task)
        {
            try
            {
                logger.LogInformation("Creating container registry '{RegistryName}'...", Name);

                var (registryName, endpoint) = await CreateRegistryAsync(context);

                _resolvedName = registryName;
                _resolvedEndpoint = endpoint;

                logger.LogInformation(
                    "Container registry '{RegistryName}' created with endpoint '{Endpoint}'",
                    registryName, endpoint);

                await task.CompleteAsync(
                    $"Container registry **{registryName}** provisioned at {endpoint}",
                    CompletionState.Completed,
                    context.CancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to provision container registry '{RegistryName}'", Name);
                await task.FailAsync(ex.Message, cancellationToken: context.CancellationToken);
                throw;
            }
        }
    }

    /// <summary>
    /// Logs in to the container registry.
    /// </summary>
    private async Task LoginToRegistryAsync(PipelineStepContext context)
    {
        if (LoginCallback is null)
        {
            context.Logger.LogWarning("No login callback configured for registry '{RegistryName}'. Skipping authentication.", Name);
            return;
        }

        await LoginCallback(context, this).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets the resolved registry values after provisioning.
    /// </summary>
    /// <param name="name">The registry name.</param>
    /// <param name="endpoint">The registry endpoint.</param>
    protected void SetResolvedValues(string name, string endpoint)
    {
        _resolvedName = name;
        _resolvedEndpoint = endpoint;
    }
}
