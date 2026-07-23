// Licensed under the MIT License.

#pragma warning disable ASPIREPIPELINES001 // Pipeline APIs are experimental
#pragma warning disable ASPIRECOMPUTE002  // IComputeEnvironmentResource is experimental
#pragma warning disable ASPIRECOMPUTE003  // IContainerRegistry is experimental

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using EmmittJ.Aspire.Hosting.Pulumi;
using EmmittJ.Aspire.Hosting.Pulumi.Azure;
using Pulumi;
using Pulumi.Automation;
using Pulumi.AzureNative.Authorization;
using Pulumi.AzureNative.ManagedIdentity;
using Pulumi.AzureNative.Resources;
using Pulumi.AzureNative.Web;
using Pulumi.Random;

namespace EmmittJ.Aspire.Hosting.Pulumi.Azure.AppService;

/// <summary>
/// A Pulumi-managed compute environment that deploys Aspire compute resources to Azure App Service.
/// </summary>
/// <remarks>
/// Container images are pushed to a dedicated <see cref="PulumiAzureContainerRegistryResource"/> stack first,
/// then the environment's Pulumi program provisions a resource group, user-assigned managed identity (granted
/// AcrPull), a Linux App Service plan shared by all apps, and a Web App per compute resource.
/// </remarks>
public sealed class PulumiAzureAppServiceEnvironmentResource : PulumiEnvironmentResource
{
    // Built-in "AcrPull" role definition id.
    private const string AcrPullRoleDefinitionId = "/providers/Microsoft.Authorization/roleDefinitions/7f951dda-4ed3-4680-a7ca-43fe172d538d";

    private readonly PulumiAzureContainerRegistryResource _registry;
    private ResourceGroup? _resourceGroup;
    private AppServicePlan? _appServicePlan;
    private UserAssignedIdentity? _managedIdentity;

    /// <summary>
    /// Initializes a new instance of the <see cref="PulumiAzureAppServiceEnvironmentResource"/> class.
    /// </summary>
    /// <param name="name">The environment resource name. Used as the default Pulumi project name.</param>
    /// <param name="projectName">The Pulumi project name. Defaults to <paramref name="name"/>.</param>
    /// <remarks>
    /// The Pulumi stack (the Aspire deployment environment such as <c>dev</c>/<c>staging</c>/<c>prod</c>) is
    /// resolved at deploy time. Azure physical resource names are prefixed with <c>{project}-{stack}</c> so two
    /// projects deploying the same environment do not collide on resource group or plan names.
    /// </remarks>
    public PulumiAzureAppServiceEnvironmentResource(string name, string? projectName = null)
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

    /// <summary>Gets or sets the App Service plan SKU name. Defaults to <c>P0v3</c> (Premium v3).</summary>
    /// <remarks>
    /// The default matches Aspire's Azure App Service integration: Premium v3 is the smallest tier that
    /// supports Linux custom containers with per-site scaling.
    /// </remarks>
    public string PlanSkuName { get; set; } = "P0v3";

    /// <summary>Gets or sets the App Service plan SKU tier. Defaults to <c>Premium0V3</c>.</summary>
    public string PlanSkuTier { get; set; } = "Premium0V3";

    /// <summary>
    /// Gets or sets whether HTTP endpoints are upgraded to HTTPS. Defaults to <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// App Service always terminates TLS on <c>*.azurewebsites.net</c>, so upgraded endpoints resolve to
    /// <c>https</c> URLs and the Web App is created with <c>HttpsOnly</c> enabled.
    /// </remarks>
    public bool HttpsUpgrade { get; set; } = true;

    /// <summary>Gets the Azure container registry that backs this environment.</summary>
    public PulumiAzureContainerRegistryResource Registry => _registry;

    /// <inheritdoc />
    public override IContainerRegistry ContainerRegistry => _registry;

    internal ResourceGroup? ResourceGroup => _resourceGroup;
    internal AppServicePlan? AppServicePlan => _appServicePlan;
    internal UserAssignedIdentity? ManagedIdentity => _managedIdentity;

    /// <inheritdoc />
    public override ReferenceExpression GetHostAddressExpression(EndpointReference endpointReference)
    {
        // Best-effort fallback host used only for endpoints whose owner is not a compute resource in this
        // environment (for example a cross-environment reference). Same-environment endpoints resolve to the
        // site's real hostname (which carries a unique suffix) via PulumiAzureAppServiceWebsiteContext.
        var resource = endpointReference.Resource;
        return ReferenceExpression.Create($"{resource.Name.ToLowerInvariant()}.azurewebsites.net");
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
        var appServicePlan = GetOrCreateAppServicePlan(resourceGroup);

        CreateAcrPullRoleAssignment(managedIdentity);

        // App Service site names are globally unique DNS labels ({site}.azurewebsites.net), so every site
        // gets a shared random suffix, mirroring Aspire's uniqueString(subscription, resourceGroup) suffix.
        // Deterministic keepers keyed on the stack so re-running keeps the same generated hostnames.
        var siteSuffix = new RandomString($"{PhysicalPrefix}-site-suffix", new RandomStringArgs
        {
            Length = 10,
            Special = false,
            Upper = false,
            Numeric = true,
            Keepers = new InputMap<string> { ["stack"] = StackName },
        });

        var environmentContext = new PulumiAzureAppServiceEnvironmentContext(this, context, siteSuffix.Result);

        // Phase 1: register a context for every targeted resource so endpoint mappings exist before any
        // Web App is built, ensuring cross-resource endpoint references can be resolved.
        var targetedContexts = new List<PulumiAzureAppServiceWebsiteContext>();
        foreach (var compute in context.GetTargetedComputeResources())
        {
            targetedContexts.Add(environmentContext.GetOrCreateContext(compute));
        }

        // Phase 2: build each Web App. Environment-variable resolution can now read sibling mappings.
        foreach (var computeContext in targetedContexts)
        {
            await computeContext.ProcessResourceAsync().ConfigureAwait(false);
        }

        context.AddOutput("resourceGroupName", resourceGroup.Name);
        context.AddOutput("appServicePlanName", appServicePlan.Name);
    }

    // {project}-{stack} prefixes every physical Azure name (resolved at deploy time inside the Pulumi program)
    // so two projects deploying the same environment do not collide on resource group / plan names.
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

    private AppServicePlan GetOrCreateAppServicePlan(ResourceGroup resourceGroup)
    {
        if (_appServicePlan is not null)
        {
            return _appServicePlan;
        }

        var planName = $"{PhysicalPrefix}-plan";
        _appServicePlan = new AppServicePlan(planName, new AppServicePlanArgs
        {
            Name = planName,
            ResourceGroupName = resourceGroup.Name,
            Location = Location,
            Kind = "linux",
            // Reserved must be true for Linux plans; PerSiteScaling lets each site scale independently
            // even though they share the plan (matches Aspire's App Service integration).
            Reserved = true,
            PerSiteScaling = true,
            Sku = new global::Pulumi.AzureNative.Web.Inputs.SkuDescriptionArgs
            {
                Name = PlanSkuName,
                Tier = PlanSkuTier,
            },
        });

        return _appServicePlan;
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
