// Licensed under the Apache License, Version 2.0.

#pragma warning disable ASPIREPIPELINES001 // Pipeline APIs are experimental
#pragma warning disable ASPIRECOMPUTE003 // IContainerRegistry is experimental

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using EmmittJ.Aspire.Hosting.Pulumi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pulumi;
using Pulumi.Automation;
using Pulumi.AzureNative.App;
using Pulumi.AzureNative.App.Inputs;
using Pulumi.AzureNative.Authorization;
using Pulumi.AzureNative.ManagedIdentity;
using Pulumi.AzureNative.Resources;

namespace EmmittJ.Aspire.Hosting.Pulumi.Azure;

/// <summary>
/// Azure compute environment resource that deploys Aspire applications
/// to Azure Container Apps using Pulumi.
/// </summary>
/// <remarks>
/// <para>
/// This resource extends <see cref="PulumiCloudEnvironmentResource"/> and uses a separate
/// <see cref="PulumiAzureContainerRegistryResource"/> for container registry management.
/// The registry is deployed first, then images are built and pushed, and finally
/// the Container Apps are deployed.
/// </para>
/// <para>
/// The two-stage deployment pattern:
/// </para>
/// <list type="number">
/// <item>Stage 1: Container Registry (separate Pulumi stack)</item>
/// <item>Docker login to ACR</item>
/// <item>Build and push images</item>
/// <item>Stage 2: Container Apps Environment (main Pulumi stack)</item>
/// </list>
/// </remarks>
public class PulumiAzureEnvironmentResource : PulumiCloudEnvironmentResource
{
    private readonly PulumiAzureContainerRegistryResource _registry;
    private WorkspaceStack? _stack;
    private ResourceGroup? _resourceGroup;
    private ManagedEnvironment? _managedEnvironment;
    private UserAssignedIdentity? _managedIdentity;

    /// <summary>
    /// ACR Pull role definition ID (built-in Azure role).
    /// </summary>
    private const string AcrPullRoleDefinitionId = "/providers/Microsoft.Authorization/roleDefinitions/7f951dda-4ed3-4680-a7ca-43fe172d538d";

    /// <summary>
    /// Initializes a new instance of the <see cref="PulumiAzureEnvironmentResource"/> class.
    /// </summary>
    /// <param name="name">The name of the environment (used as both project name and environment name).</param>
    public PulumiAzureEnvironmentResource(string name)
        : this(name, projectName: name)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PulumiAzureEnvironmentResource"/> class
    /// with a custom project name.
    /// </summary>
    /// <param name="name">The name of the environment.</param>
    /// <param name="projectName">The Pulumi project name for grouping stacks.</param>
    public PulumiAzureEnvironmentResource(string name, string projectName)
        : this(
            name,
            CreateRegistry(name, projectName),
            projectName)
    {
    }

    /// <summary>
    /// Creates the container registry with proper naming derived from project and environment.
    /// </summary>
    private static PulumiAzureContainerRegistryResource CreateRegistry(string envName, string projectName)
    {
        return new PulumiAzureContainerRegistryResource(
            $"{envName}-registry",
            environmentName: envName,
            projectName: projectName);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PulumiAzureEnvironmentResource"/> class
    /// with a custom container registry.
    /// </summary>
    /// <param name="name">The name of the environment.</param>
    /// <param name="registry">The Azure container registry resource to use.</param>
    /// <param name="projectName">The Pulumi project name. If null, uses <paramref name="name"/>.</param>
    public PulumiAzureEnvironmentResource(
        string name,
        PulumiAzureContainerRegistryResource registry,
        string? projectName = null)
        : base(name, registry, projectName)
    {
        _registry = registry;
    }

    /// <summary>
    /// Gets or sets the Azure region for deployment.
    /// </summary>
    public string Location
    {
        get => _registry.Location;
        set => _registry.Location = value;
    }

    /// <summary>
    /// Gets or sets the resource group name for the environment.
    /// If not set, a resource group will be created.
    /// </summary>
    public string? ResourceGroupName { get; set; }

    /// <summary>
    /// Gets or sets the Container Apps Environment name.
    /// If not set, one will be created.
    /// </summary>
    public string? ManagedEnvironmentName { get; set; }

    /// <summary>
    /// Gets the Azure container registry resource.
    /// </summary>
    public new PulumiAzureContainerRegistryResource ContainerRegistry => _registry;

    /// <summary>
    /// Gets the created Pulumi resource group.
    /// Available after translation.
    /// </summary>
    internal ResourceGroup? ResourceGroup => _resourceGroup;

    /// <summary>
    /// Gets the created Pulumi managed environment.
    /// Available after translation.
    /// </summary>
    internal ManagedEnvironment? ManagedEnvironment => _managedEnvironment;

    /// <summary>
    /// Gets the created Pulumi managed identity.
    /// Available after translation.
    /// </summary>
    internal UserAssignedIdentity? ManagedIdentity => _managedIdentity;

    /// <inheritdoc />
    public override ReferenceExpression GetHostAddressExpression(EndpointReference endpointReference)
    {
        var resource = endpointReference.Resource;
        // Azure Container Apps format: https://{app-name}.{default-domain}
        // The actual domain is set by Azure, but we can reference it via the app's FQDN
        return ReferenceExpression.Create($"{resource.Name.ToLowerInvariant()}.azurecontainerapps.io");
    }

    /// <inheritdoc />
    protected override async Task AddEnvironmentStepsAsync(
        PipelineStepFactoryContext factoryContext,
        List<PipelineStep> steps)
    {
        // Main deploy step for Container Apps
        var deployStep = new PipelineStep
        {
            Name = PulumiWellKnownPipelineSteps.Deploy(Name),
            Description = $"Deploys Container Apps to {Name} using Pulumi.",
            Action = DeployAsync,
            Resource = this,
            Tags = [PulumiWellKnownPipelineSteps.PulumiTag]
        };
        deployStep.RequiredBy(WellKnownPipelineSteps.Deploy);
        // Deploy depends on Push (not Build) - this ensures images are pushed to registry first
        // The registry step is RequiredBy(PushPrereq), so the dependency chain is:
        // pulumi-deploy-registry -> PushPrereq -> push-* -> Push -> pulumi-deploy-env
        deployStep.DependsOn(WellKnownPipelineSteps.Push);
        steps.Add(deployStep);

        // Print summary step
        var printSummaryStep = new PipelineStep
        {
            Name = PulumiWellKnownPipelineSteps.PrintSummary(Name),
            Description = $"Prints deployment summary for {Name}.",
            Action = PrintSummaryAsync,
            Resource = this,
            Tags = [PulumiWellKnownPipelineSteps.PrintSummaryTag],
            DependsOnSteps = [PulumiWellKnownPipelineSteps.Deploy(Name)],
            RequiredBySteps = [WellKnownPipelineSteps.Deploy]
        };
        steps.Add(printSummaryStep);

        await Task.CompletedTask;
    }

    /// <summary>
    /// Executes the deploy step using the Pulumi Automation API.
    /// </summary>
    private async Task DeployAsync(PipelineStepContext context)
    {
        var logger = context.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger<PulumiAzureEnvironmentResource>();

        var task = await context.ReportingStep.CreateTaskAsync(
            $"Deploying to **{Name}** with Pulumi", context.CancellationToken);

        await using (task)
        {
            try
            {
                var stack = await GetOrCreateStackAsync(context, logger);

                logger.LogInformation("Running 'pulumi up' for stack '{StackName}'...", Name);

                var result = await stack.UpAsync(new UpOptions
                {
                    OnStandardOutput = msg => logger.LogInformation("{Message}", msg),
                    OnStandardError = msg => logger.LogError("{Message}", msg)
                }, context.CancellationToken);

                logger.LogInformation(
                    "Deployment to stack '{StackName}' completed. Summary: {Summary}",
                    Name,
                    result.Summary.Message);

                await task.CompleteAsync(
                    $"Deployment to **{Name}** completed successfully.",
                    CompletionState.Completed,
                    context.CancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Deployment to stack '{StackName}' failed", Name);
                await task.FailAsync(ex.Message, cancellationToken: context.CancellationToken);
                throw;
            }
        }
    }

    /// <summary>
    /// Creates the Pulumi stack for the Container Apps environment.
    /// </summary>
    private async Task<WorkspaceStack> GetOrCreateStackAsync(PipelineStepContext context, ILogger logger)
    {
        if (_stack is not null)
        {
            return _stack;
        }

        // Use the centralized naming from the base class
        // All stacks are grouped under PulumiProjectName in the Pulumi console
        var projectName = PulumiProjectName;
        var stackName = ResourcePrefix;

        logger.LogDebug(
            "Creating Pulumi stack '{StackName}' for project '{ProjectName}'",
            stackName, projectName);

        // Create the inline program that will create Container App resources
        var program = PulumiFn.Create(async () =>
        {
            var publishingContext = new PulumiPublishingContext(
                context.Model,
                this,
                context,
                context.ExecutionContext,
                logger);

            // Create the environment resources
            await CreateResourcesAsync(publishingContext);

            // Return outputs
            return publishingContext.BuildOutputs();
        });

        // Create or select the stack
        var workDir = Path.Combine(Path.GetTempPath(), "pulumi-aspire", projectName);
        Directory.CreateDirectory(workDir);

        _stack = await LocalWorkspace.CreateOrSelectStackAsync(
            new InlineProgramArgs(projectName, stackName, program)
            {
                WorkDir = workDir
            },
            context.CancellationToken);

        // Configure Azure provider
        await _stack.SetConfigAsync("azure-native:location", new ConfigValue(Location), context.CancellationToken);

        return _stack;
    }

    /// <summary>
    /// Creates the Azure Container Apps resources within the Pulumi program.
    /// </summary>
    public override async Task CreateResourcesAsync(PulumiPublishingContext context)
    {
        context.Logger.LogInformation(
            "Creating Azure Container Apps for environment '{Name}' in location '{Location}'",
            Name, Location);

        // Create shared infrastructure
        var resourceGroup = GetOrCreateResourceGroup(context);
        var managedIdentity = GetOrCreateManagedIdentity(context, resourceGroup);
        var managedEnvironment = GetOrCreateManagedEnvironment(context, resourceGroup);

        // Create role assignment for ACR Pull (if registry is available)
        CreateAcrPullRoleAssignment(context, managedIdentity);

        // Process each compute resource
        foreach (var resource in context.Model.GetComputeResources())
        {
            if (resource is not IComputeResource computeResource)
            {
                continue;
            }

            // Skip if marked to skip
            if (computeResource.TryGetAnnotationsOfType<SkipPulumiTranslationAnnotation>(out _))
            {
                context.Logger.LogDebug(
                    "Skipping resource '{Name}' (marked with SkipPulumiTranslationAnnotation)",
                    computeResource.Name);
                continue;
            }

            await CreateContainerAppAsync(context, computeResource);
        }

        // Add outputs
        context.AddOutput("resourceGroupName", resourceGroup.Name);
        context.AddOutput("managedEnvironmentName", managedEnvironment.Name);

        // Add registry information from the already-deployed registry
        if (_registry.ResolvedName is not null)
        {
            context.AddOutput("containerRegistryName", _registry.ResolvedName);
        }
        if (_registry.ResolvedEndpoint is not null)
        {
            context.AddOutput("containerRegistryLoginServer", _registry.ResolvedEndpoint);
        }

        context.Logger.LogInformation(
            "Created Azure Container Apps for environment '{Name}'",
            Name);
    }

    /// <summary>
    /// Prints a summary of the deployment outputs.
    /// </summary>
    private async Task PrintSummaryAsync(PipelineStepContext context)
    {
        var logger = context.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger<PulumiAzureEnvironmentResource>();

        if (_stack is null)
        {
            logger.LogWarning("No stack available for print summary");
            return;
        }

        try
        {
            var outputs = await _stack.GetOutputsAsync(context.CancellationToken);

            if (outputs.Count == 0)
            {
                logger.LogInformation("No outputs available for stack '{StackName}'", Name);
                return;
            }

            logger.LogInformation("Stack '{StackName}' outputs:", Name);
            foreach (var (key, value) in outputs)
            {
                var displayValue = value.IsSecret ? "***" : value.Value?.ToString() ?? "(null)";
                logger.LogInformation("  {Key}: {Value}", key, displayValue);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get outputs for stack '{StackName}'", Name);
        }
    }

    /// <summary>
    /// Gets or creates the Azure resource group for the Container Apps environment.
    /// </summary>
    /// <remarks>
    /// This resource group is separate from the registry's resource group.
    /// Registry uses <c>{ResourcePrefix}-registry-rg</c>, environment uses <c>{ResourcePrefix}-rg</c>.
    /// </remarks>
    private ResourceGroup GetOrCreateResourceGroup(PulumiPublishingContext context)
    {
        if (_resourceGroup is not null)
        {
            return _resourceGroup;
        }

        var resourceGroupName = ResourceGroupName ?? $"{ResourcePrefix}-rg";

        _resourceGroup = new ResourceGroup(resourceGroupName, new ResourceGroupArgs
        {
            ResourceGroupName = resourceGroupName,
            Location = Location,
        });

        context.Logger.LogDebug("Creating resource group '{Name}'", resourceGroupName);
        return _resourceGroup;
    }

    /// <summary>
    /// Gets or creates the user-assigned managed identity for Container Apps.
    /// This identity is used for authenticating to ACR.
    /// </summary>
    private UserAssignedIdentity GetOrCreateManagedIdentity(
        PulumiPublishingContext context,
        ResourceGroup resourceGroup)
    {
        if (_managedIdentity is not null)
        {
            return _managedIdentity;
        }

        var identityName = $"{ResourcePrefix}-identity";

        _managedIdentity = new UserAssignedIdentity(identityName, new UserAssignedIdentityArgs
        {
            ResourceName = identityName,
            ResourceGroupName = resourceGroup.Name,
            Location = Location
        });

        context.Logger.LogDebug("Created managed identity '{Name}'", identityName);
        return _managedIdentity;
    }

    /// <summary>
    /// Creates a role assignment granting the managed identity ACR Pull permissions.
    /// </summary>
    private void CreateAcrPullRoleAssignment(
        PulumiPublishingContext context,
        UserAssignedIdentity managedIdentity)
    {
        // Only create if we have a registry with resolved values
        if (_registry.ResolvedName is null || _registry.ResolvedResourceGroupName is null)
        {
            context.Logger.LogWarning(
                "Cannot create ACR Pull role assignment: registry name or resource group not resolved");
            return;
        }

        // The scope is the ACR resource ID
        // Format: /subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.ContainerRegistry/registries/{name}
        // Use the resolved resource group name from the registry stack
        var registryResourceGroup = _registry.ResolvedResourceGroupName;

        // Get subscription ID from Azure provider context (using the identity's ID to extract it)
        var subscriptionId = managedIdentity.Id.Apply(id =>
        {
            // ID format: /subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.ManagedIdentity/userAssignedIdentities/{name}
            var parts = id.Split('/');
            return parts.Length > 2 ? parts[2] : string.Empty;
        });

        var scope = subscriptionId.Apply(sub =>
            $"/subscriptions/{sub}/resourceGroups/{registryResourceGroup}/providers/Microsoft.ContainerRegistry/registries/{_registry.ResolvedName}");

        var roleAssignment = new RoleAssignment($"{ResourcePrefix}-acr-pull", new RoleAssignmentArgs
        {
            PrincipalId = managedIdentity.PrincipalId,
            PrincipalType = "ServicePrincipal",
            RoleDefinitionId = subscriptionId.Apply(sub =>
                $"/subscriptions/{sub}{AcrPullRoleDefinitionId}"),
            Scope = scope
        });

        context.Logger.LogDebug("Created ACR Pull role assignment for identity");
    }

    /// <summary>
    /// Gets or creates the Azure Container Apps managed environment.
    /// </summary>
    private ManagedEnvironment GetOrCreateManagedEnvironment(
        PulumiPublishingContext context,
        ResourceGroup resourceGroup)
    {
        if (_managedEnvironment is not null)
        {
            return _managedEnvironment;
        }

        var envName = ManagedEnvironmentName ?? $"{ResourcePrefix}-env";

        _managedEnvironment = new ManagedEnvironment(envName, new ManagedEnvironmentArgs
        {
            EnvironmentName = envName,
            ResourceGroupName = resourceGroup.Name,
            Location = Location
        });

        context.Logger.LogDebug("Created managed environment '{Name}'", envName);
        return _managedEnvironment;
    }

    /// <summary>
    /// Creates a Container App for the compute resource using the context-based pattern.
    /// </summary>
    private async Task CreateContainerAppAsync(
        PulumiPublishingContext context,
        IComputeResource computeResource)
    {
        context.Logger.LogDebug("Creating Container App for '{Name}'", computeResource.Name);

        // Create the Azure compute resource context which will process env vars, args, endpoints
        // and build the ContainerApp resource
        var computeContext = new PulumiAzureComputeResourceContext(
            computeResource,
            context,
            this);

        // Process the resource and build the ContainerApp
        // This collects env vars, args, endpoints and creates the Pulumi resource
        await computeContext.ProcessResourceAsync(context.PipelineContext.CancellationToken);

        context.Logger.LogInformation(
            "Created Container App for resource '{ResourceName}'",
            computeResource.Name);
    }
}
