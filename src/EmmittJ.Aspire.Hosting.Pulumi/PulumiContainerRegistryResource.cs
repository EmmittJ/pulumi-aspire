// Licensed under the Apache License, Version 2.0.

#pragma warning disable ASPIREPIPELINES001 // Pipeline APIs are experimental
#pragma warning disable ASPIRECOMPUTE003  // IContainerRegistry is experimental

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EmmittJ.Aspire.Hosting.Pulumi;

/// <summary>
/// Base class for a container registry provisioned through Pulumi as a separate stack before the main environment.
/// </summary>
/// <remarks>
/// <para>
/// The registry is provisioned by its own pipeline step (required by <c>push-prereq</c>) so that images can be
/// pushed before the environment is deployed. Unlike the environment, a registry is a plain resource and is not a
/// compute environment.
/// </para>
/// <para>
/// The <see cref="IContainerRegistry.Name"/> and <see cref="IContainerRegistry.Endpoint"/> expressions reflect the
/// values resolved during provisioning. They are consumed by Aspire's framework-owned image push step, which runs
/// after the registry's provision and login steps.
/// </para>
/// </remarks>
public abstract class PulumiContainerRegistryResource : Resource, IContainerRegistry
{
    private string? _resolvedName;
    private string? _resolvedEndpoint;

    /// <summary>
    /// Initializes a new instance of the <see cref="PulumiContainerRegistryResource"/> class.
    /// </summary>
    /// <param name="name">The registry resource name.</param>
    protected PulumiContainerRegistryResource(string name)
        : base(name)
    {
        Annotations.Add(new PipelineStepAnnotation(CreatePipelineSteps));
    }

    /// <summary>Gets the Pulumi project name that groups the registry stack.</summary>
    public abstract string PulumiProjectName { get; }

    /// <summary>Gets the Pulumi stack name used to provision the registry.</summary>
    public abstract string StackName { get; }

    /// <summary>Gets or sets the working directory for Pulumi operations. When unset, a per-project temp directory is used.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Gets or sets the callback that authenticates Docker to the registry after it is provisioned.
    /// </summary>
    public Func<PipelineStepContext, IContainerRegistry, Task>? LoginCallback { get; set; }

    /// <summary>Gets the registry name resolved during provisioning, or <see langword="null"/> before provisioning.</summary>
    public string? ResolvedName => _resolvedName;

    /// <summary>Gets the registry login endpoint resolved during provisioning, or <see langword="null"/> before provisioning.</summary>
    public string? ResolvedEndpoint => _resolvedEndpoint;

    // No registry-specific fallback (e.g. "{name}.azurecr.io"). When unresolved, these project to empty so the
    // framework's push-prereq validation surfaces the missing registry instead of silently using a wrong host.
    ReferenceExpression IContainerRegistry.Name => ReferenceExpression.Create($"{_resolvedName}");
    ReferenceExpression IContainerRegistry.Endpoint => ReferenceExpression.Create($"{_resolvedEndpoint}");

    /// <summary>
    /// Provisions the container registry and returns its resolved name and login endpoint. Implemented by providers.
    /// </summary>
    /// <param name="context">The pipeline step context.</param>
    protected abstract Task<(string Name, string Endpoint)> CreateRegistryAsync(PipelineStepContext context);

    /// <summary>
    /// Destroys the container registry stack. Implemented by providers, typically by running
    /// <c>pulumi destroy</c> against the same project/stack used to provision the registry.
    /// </summary>
    /// <param name="context">The pipeline step context.</param>
    protected abstract Task DestroyRegistryAsync(PipelineStepContext context);

    private IEnumerable<PipelineStep> CreatePipelineSteps(PipelineStepFactoryContext factoryContext)
    {
        var steps = new List<PipelineStep>
        {
            new()
            {
                Name = PulumiPipelineSteps.DeployRegistry(Name),
                Description = $"Provisions container registry '{Name}' using Pulumi.",
                Action = ProvisionRegistryAsync,
                Tags = [WellKnownPipelineTags.ProvisionInfrastructure],
                DependsOnSteps = [WellKnownPipelineSteps.PublishPrereq],
                // Required before push-prereq so the registry exists (and login can run) before images push.
                RequiredBySteps = [WellKnownPipelineSteps.PushPrereq],
                Resource = this,
            },
            new()
            {
                Name = PulumiPipelineSteps.DestroyRegistry(Name),
                Description = $"Destroys container registry '{Name}' using Pulumi.",
                Action = DestroyRegistryStepAsync,
                // The registry is its own Pulumi stack, so it needs its own destroy step. Without this,
                // `aspire destroy` would tear down the environment stack but leave the registry orphaned.
                DependsOnSteps = [WellKnownPipelineSteps.DestroyPrereq],
                RequiredBySteps = [WellKnownPipelineSteps.Destroy],
                Resource = this,
            },
        };

        if (LoginCallback is not null)
        {
            steps.Add(new PipelineStep
            {
                Name = PulumiPipelineSteps.LoginRegistry(Name),
                Description = $"Authenticates to container registry '{Name}'.",
                Action = LoginToRegistryAsync,
                Tags = [WellKnownPipelineTags.ProvisionInfrastructure],
                DependsOnSteps = [PulumiPipelineSteps.DeployRegistry(Name)],
                RequiredBySteps = [WellKnownPipelineSteps.PushPrereq],
                Resource = this,
            });
        }

        return steps;
    }

    private async Task ProvisionRegistryAsync(PipelineStepContext context)
    {
        var logger = context.Services.GetRequiredService<ILoggerFactory>().CreateLogger<PulumiContainerRegistryResource>();

        var task = await context.ReportingStep.CreateTaskAsync(
            $"Provisioning container registry **{Name}**", context.CancellationToken).ConfigureAwait(false);

        await using (task.ConfigureAwait(false))
        {
            try
            {
                var (registryName, endpoint) = await CreateRegistryAsync(context).ConfigureAwait(false);
                _resolvedName = registryName;
                _resolvedEndpoint = endpoint;

                logger.LogInformation("Container registry '{RegistryName}' provisioned at '{Endpoint}'.", registryName, endpoint);

                await task.CompleteAsync(
                    $"Container registry **{registryName}** provisioned at {endpoint}.",
                    CompletionState.Completed,
                    context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await task.CompleteAsync(ex.Message, CompletionState.CompletedWithError, context.CancellationToken).ConfigureAwait(false);
                throw;
            }
        }
    }

    private async Task LoginToRegistryAsync(PipelineStepContext context)
    {
        if (LoginCallback is null)
        {
            return;
        }

        await LoginCallback(context, this).ConfigureAwait(false);
    }

    private async Task DestroyRegistryStepAsync(PipelineStepContext context)
    {
        var task = await context.ReportingStep.CreateTaskAsync(
            $"Destroying container registry **{Name}**", context.CancellationToken).ConfigureAwait(false);

        await using (task.ConfigureAwait(false))
        {
            try
            {
                await DestroyRegistryAsync(context).ConfigureAwait(false);

                await task.CompleteAsync(
                    $"Container registry **{Name}** destroyed.",
                    CompletionState.Completed,
                    context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await task.CompleteAsync(ex.Message, CompletionState.CompletedWithError, context.CancellationToken).ConfigureAwait(false);
                throw;
            }
        }
    }

    /// <summary>Sets the resolved registry values. Providers may call this when they resolve values out of band.</summary>
    /// <param name="name">The registry name.</param>
    /// <param name="endpoint">The registry login endpoint.</param>
    protected void SetResolvedValues(string name, string endpoint)
    {
        _resolvedName = name;
        _resolvedEndpoint = endpoint;
    }
}
