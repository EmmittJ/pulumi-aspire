// Licensed under the MIT License.

#pragma warning disable ASPIREPIPELINES001 // Pipeline APIs are experimental
#pragma warning disable ASPIRECOMPUTE002  // IComputeEnvironmentResource is experimental
#pragma warning disable ASPIRECOMPUTE003  // IContainerRegistry is experimental

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using EmmittJ.Aspire.Hosting.Pulumi;
using Pulumi;
using Pulumi.Automation;
using Pulumi.AzureNative.App;
using Pulumi.AzureNative.Authorization;
using Pulumi.AzureNative.ManagedIdentity;
using Pulumi.AzureNative.Resources;

namespace EmmittJ.Aspire.Hosting.Pulumi.Azure.AppContainers;

/// <summary>
/// A Pulumi-managed compute environment that deploys Aspire compute resources to Azure Container Apps.
/// </summary>
/// <remarks>
/// Container images are pushed to a dedicated <see cref="PulumiAzureContainerRegistryResource"/> stack first,
/// then the environment's Pulumi program provisions a resource group, user-assigned managed identity (granted
/// AcrPull), a Container Apps managed environment, and a Container App per compute resource.
/// </remarks>
public sealed class PulumiAzureContainerAppEnvironmentResource : PulumiEnvironmentResource
{
    // Built-in "AcrPull" role definition id.
    private const string AcrPullRoleDefinitionId = "/providers/Microsoft.Authorization/roleDefinitions/7f951dda-4ed3-4680-a7ca-43fe172d538d";

    private readonly PulumiAzureContainerRegistryResource _registry;
    private ResourceGroup? _resourceGroup;
    private ManagedEnvironment? _managedEnvironment;
    private UserAssignedIdentity? _managedIdentity;

    /// <summary>
    /// Initializes a new instance of the <see cref="PulumiAzureContainerAppEnvironmentResource"/> class.
    /// </summary>
    /// <param name="name">The environment resource name. Used as the default Pulumi project name.</param>
    /// <param name="projectName">The Pulumi project name. Defaults to <paramref name="name"/>.</param>
    /// <remarks>
    /// The Pulumi stack (the Aspire deployment environment such as <c>dev</c>/<c>staging</c>/<c>prod</c>) is
    /// resolved at deploy time. Azure physical resource names are prefixed with <c>{project}-{stack}</c> so two
    /// projects deploying the same environment do not collide on resource group or managed environment names.
    /// </remarks>
    public PulumiAzureContainerAppEnvironmentResource(string name, string? projectName = null)
        : base(name, projectName)
    {
        _registry = new PulumiAzureContainerRegistryResource(
            $"{name}-registry",
            pulumiProjectName: PulumiProjectName,
            // The registry stack tracks the environment stack (default or WithStackName override) plus a suffix.
            stackNameResolver: services => $"{ResolveStackName(services)}-registry",
            location: "eastus");
    }

    /// <summary>Gets or sets the Azure region for the environment and its registry.</summary>
    public string Location
    {
        get => _registry.Location;
        set => _registry.Location = value;
    }

    /// <summary>Gets or sets an existing resource group name. When unset, one is created.</summary>
    public string? ResourceGroupName { get; set; }

    /// <summary>Gets or sets an existing Container Apps managed environment name. When unset, one is created.</summary>
    public string? ManagedEnvironmentName { get; set; }

    /// <summary>Gets the Azure container registry that backs this environment.</summary>
    public PulumiAzureContainerRegistryResource Registry => _registry;

    /// <inheritdoc />
    public override IContainerRegistry ContainerRegistry => _registry;

    internal ResourceGroup? ResourceGroup => _resourceGroup;
    internal ManagedEnvironment? ManagedEnvironment => _managedEnvironment;
    internal UserAssignedIdentity? ManagedIdentity => _managedIdentity;

    /// <inheritdoc />
    public override ReferenceExpression GetHostAddressExpression(EndpointReference endpointReference)
    {
        // Best-effort fallback host used only for endpoints whose owner is not a compute resource in this
        // environment (for example a cross-environment reference). Same-environment endpoints resolve to the
        // managed environment's real default domain via PulumiAzureContainerAppContext.
        var resource = endpointReference.Resource;
        return ReferenceExpression.Create($"{resource.Name.ToLowerInvariant()}.azurecontainerapps.io");
    }

    /// <inheritdoc />
    protected override async Task ConfigureStackAsync(WorkspaceStack stack, PipelineStepContext context)
    {
        await stack.SetConfigAsync("azure-native:location", new ConfigValue(Location), context.CancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task CreateStackResourcesAsync(PulumiPublishingContext context)
    {
        var resourceGroup = GetOrCreateResourceGroup();
        var managedIdentity = GetOrCreateManagedIdentity(resourceGroup);
        var managedEnvironment = GetOrCreateManagedEnvironment(resourceGroup);

        CreateAcrPullRoleAssignment(managedIdentity);

        var environmentContext = new PulumiAzureContainerAppEnvironmentContext(this, context, managedEnvironment.DefaultDomain);

        // Phase 1: register a context for every targeted resource so endpoint mappings exist before any
        // Container App is built. This mirrors how Azure Container Apps registers all contexts during prepare
        // and then defers BuildContainerApp, ensuring cross-resource endpoint references can be resolved.
        var targetedContexts = new List<PulumiAzureContainerAppContext>();
        foreach (var compute in context.GetTargetedComputeResources())
        {
            targetedContexts.Add(environmentContext.GetOrCreateContext(compute));
        }

        // Phase 2: build each Container App. Environment-variable resolution can now read sibling mappings.
        foreach (var computeContext in targetedContexts)
        {
            await computeContext.ProcessResourceAsync().ConfigureAwait(false);
        }

        context.AddOutput("resourceGroupName", resourceGroup.Name);
        context.AddOutput("managedEnvironmentName", managedEnvironment.Name);
    }

    // {project}-{env} is the Pulumi stack name (and Aspire resource name). Validate each part against Pulumi's
    // naming rules before composing so an invalid project or environment surfaces a clear error attributed to
    // the right argument, rather than failing later inside the Automation API.
    // {project}-{stack} prefixes every physical Azure name (resolved at deploy time inside the Pulumi program)
    // so two projects deploying the same environment do not collide on resource group / managed environment names.
    private string PhysicalPrefix => $"{PulumiProjectName}-{StackName}";

    private ResourceGroup GetOrCreateResourceGroup()
    {
        if (_resourceGroup is not null)
        {
            return _resourceGroup;
        }

        var resourceGroupName = ResourceGroupName ?? $"{PhysicalPrefix}-rg";
        _resourceGroup = new ResourceGroup(resourceGroupName, new ResourceGroupArgs
        {
            ResourceGroupName = resourceGroupName,
            Location = Location,
        });

        return _resourceGroup;
    }

    private UserAssignedIdentity GetOrCreateManagedIdentity(ResourceGroup resourceGroup)
    {
        if (_managedIdentity is not null)
        {
            return _managedIdentity;
        }

        var identityName = $"{PhysicalPrefix}-identity";
        _managedIdentity = new UserAssignedIdentity(identityName, new UserAssignedIdentityArgs
        {
            ResourceName = identityName,
            ResourceGroupName = resourceGroup.Name,
            Location = Location,
        });

        return _managedIdentity;
    }

    private ManagedEnvironment GetOrCreateManagedEnvironment(ResourceGroup resourceGroup)
    {
        if (_managedEnvironment is not null)
        {
            return _managedEnvironment;
        }

        var envName = ManagedEnvironmentName ?? $"{PhysicalPrefix}-env";
        _managedEnvironment = new ManagedEnvironment(envName, new ManagedEnvironmentArgs
        {
            EnvironmentName = envName,
            ResourceGroupName = resourceGroup.Name,
            Location = Location,
        });

        return _managedEnvironment;
    }

    private void CreateAcrPullRoleAssignment(UserAssignedIdentity managedIdentity)
    {
        if (_registry.ResolvedName is null || _registry.ResolvedResourceGroupName is null)
        {
            // Registry not yet resolved (provision step did not run). The role assignment is skipped; the
            // managed identity still exists but won't have AcrPull until the registry is provisioned.
            return;
        }

        var registryResourceGroup = _registry.ResolvedResourceGroupName;

        // Extract the subscription id from the identity's ARM id:
        //   /subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.ManagedIdentity/userAssignedIdentities/{name}
        var subscriptionId = managedIdentity.Id.Apply(id =>
        {
            var parts = id.Split('/');
            return parts.Length > 2 ? parts[2] : string.Empty;
        });

        var scope = subscriptionId.Apply(sub =>
            $"/subscriptions/{sub}/resourceGroups/{registryResourceGroup}/providers/Microsoft.ContainerRegistry/registries/{_registry.ResolvedName}");

        _ = new RoleAssignment($"{PhysicalPrefix}-acr-pull", new RoleAssignmentArgs
        {
            PrincipalId = managedIdentity.PrincipalId,
            PrincipalType = "ServicePrincipal",
            RoleDefinitionId = subscriptionId.Apply(sub => $"/subscriptions/{sub}{AcrPullRoleDefinitionId}"),
            Scope = scope,
        });
    }
}
