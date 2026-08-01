// Licensed under the MIT License.

namespace EmmittJ.Aspire.Hosting.Pulumi.Azure;

/// <summary>
/// How an ARM resource type maps onto the Pulumi <c>azure-native</c> provider: the resource token, the
/// type-specific name argument, the parent-link argument for child resources, and the token of the
/// corresponding <c>get*</c> invoke used to read properties back.
/// </summary>
public sealed record AzureNativeTypeMapping
{
    /// <summary>Gets the azure-native resource token (for example <c>azure-native:app:ContainerApp</c>).</summary>
    public required string Token { get; init; }

    /// <summary>Gets the name of the type-specific resource-name input (for example <c>containerAppName</c>).</summary>
    public required string NameArgName { get; init; }

    /// <summary>
    /// Gets the name of the input that links a child resource to its parent by name (for example
    /// <c>environmentName</c> for <c>Microsoft.App/managedEnvironments/dotNetComponents</c>).
    /// <see langword="null"/> for top-level resources.
    /// </summary>
    public string? ParentArgName { get; init; }

    /// <summary>Gets the azure-native invoke token used to read the resource's properties (for example <c>azure-native:app:getContainerApp</c>).</summary>
    public required string GetInvokeToken { get; init; }

    /// <summary>
    /// Gets whether the resource takes a <c>resourceGroupName</c> input. Extension resource types such as
    /// role assignments are addressed through a <c>scope</c> input instead.
    /// </summary>
    public bool RequiresResourceGroupName { get; init; } = true;
}

/// <summary>
/// Data-driven catalog mapping ARM resource types (<c>{namespace}/{type}[/{childType}]</c>) to Pulumi
/// <c>azure-native</c> tokens.
/// </summary>
/// <remarks>
/// <para>
/// The default rule derives the token from the ARM type: the module is the lowercased provider namespace
/// segment after <c>Microsoft.</c>, and the resource name is the singularized, PascalCased last type
/// segment (<c>Microsoft.App/containerApps</c> → <c>azure-native:app:ContainerApp</c>). The explicit
/// override table covers everything the heuristic gets wrong (legacy Web types, identity's
/// <c>resourceName</c> argument, config-singleton child types) and pins every type Aspire 13.4.6 emits for
/// the Azure Container Apps and App Service environments, so mapping regressions surface in the catalog
/// tests rather than at deploy time.
/// </para>
/// <para>
/// Tokens are unversioned by default: the azure-native provider resolves them to its curated default API
/// version, which tracks close to the versions Aspire emits. Explicit versioned tokens
/// (<c>azure-native:app/v20250701:ContainerApp</c>) can be pinned per ARM type + API version through
/// <see cref="AddOverride"/> when the curated version drifts in a breaking way.
/// </para>
/// </remarks>
public static class AzureNativeTypeCatalog
{
    private static readonly Dictionary<string, AzureNativeTypeMapping> Overrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.ManagedIdentity/userAssignedIdentities"] = new()
        {
            Token = "azure-native:managedidentity:UserAssignedIdentity",
            NameArgName = "resourceName",
            GetInvokeToken = "azure-native:managedidentity:getUserAssignedIdentity",
        },
        ["Microsoft.Authorization/roleAssignments"] = new()
        {
            Token = "azure-native:authorization:RoleAssignment",
            NameArgName = "roleAssignmentName",
            GetInvokeToken = "azure-native:authorization:getRoleAssignment",
            RequiresResourceGroupName = false,
        },
        ["Microsoft.App/managedEnvironments"] = new()
        {
            Token = "azure-native:app:ManagedEnvironment",
            NameArgName = "environmentName",
            GetInvokeToken = "azure-native:app:getManagedEnvironment",
        },
        ["Microsoft.App/managedEnvironments/dotNetComponents"] = new()
        {
            Token = "azure-native:app:DotNetComponent",
            NameArgName = "name",
            ParentArgName = "environmentName",
            GetInvokeToken = "azure-native:app:getDotNetComponent",
        },
        ["Microsoft.Web/serverfarms"] = new()
        {
            Token = "azure-native:web:AppServicePlan",
            NameArgName = "name",
            GetInvokeToken = "azure-native:web:getAppServicePlan",
        },
        ["Microsoft.Web/sites"] = new()
        {
            Token = "azure-native:web:WebApp",
            NameArgName = "name",
            GetInvokeToken = "azure-native:web:getWebApp",
        },
        ["Microsoft.Web/sites/sitecontainers"] = new()
        {
            Token = "azure-native:web:WebAppSiteContainer",
            NameArgName = "containerName",
            ParentArgName = "name",
            GetInvokeToken = "azure-native:web:getWebAppSiteContainer",
        },
        // Microsoft.Web/sites/config is a family of singleton child resources selected by the child name
        // ('slotConfigNames', 'appsettings', 'web', ...); see MapSitesConfig.
        ["Microsoft.Web/sites/config:slotConfigNames"] = new()
        {
            Token = "azure-native:web:WebAppSlotConfigurationNames",
            NameArgName = "name",
            ParentArgName = "name",
            GetInvokeToken = "azure-native:web:listWebAppSlotConfigurationNames",
        },
        ["Microsoft.Web/sites/config:appsettings"] = new()
        {
            Token = "azure-native:web:WebAppApplicationSettings",
            NameArgName = "name",
            ParentArgName = "name",
            GetInvokeToken = "azure-native:web:listWebAppApplicationSettings",
        },
        ["Microsoft.Web/sites/config:web"] = new()
        {
            Token = "azure-native:web:WebAppConfiguration",
            NameArgName = "name",
            ParentArgName = "name",
            GetInvokeToken = "azure-native:web:getWebAppConfiguration",
        },
        ["Microsoft.OperationalInsights/workspaces"] = new()
        {
            Token = "azure-native:operationalinsights:Workspace",
            NameArgName = "workspaceName",
            GetInvokeToken = "azure-native:operationalinsights:getWorkspace",
        },
        ["Microsoft.ContainerRegistry/registries"] = new()
        {
            Token = "azure-native:containerregistry:Registry",
            NameArgName = "registryName",
            GetInvokeToken = "azure-native:containerregistry:getRegistry",
        },
        ["Microsoft.Insights/components"] = new()
        {
            Token = "azure-native:applicationinsights:Component",
            NameArgName = "resourceName",
            GetInvokeToken = "azure-native:applicationinsights:getComponent",
        },
        ["Microsoft.Portal/dashboards"] = new()
        {
            Token = "azure-native:portal:Dashboard",
            NameArgName = "dashboardName",
            GetInvokeToken = "azure-native:portal:getDashboard",
        },
    };

    /// <summary>
    /// The invoke used per ARM type when a template calls <c>listKeys()</c> on a resource symbol, and the
    /// property-name remapping (if any) between the ARM <c>listKeys</c> result shape and the invoke result.
    /// </summary>
    private static readonly Dictionary<string, string> ListKeysInvokes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.OperationalInsights/workspaces"] = "azure-native:operationalinsights:getSharedKeys",
        ["Microsoft.ContainerRegistry/registries"] = "azure-native:containerregistry:listRegistryCredentials",
    };

    /// <summary>
    /// Registers or replaces an explicit mapping override for an ARM resource type, optionally scoped to a
    /// specific API version (exact-version pins take precedence over unversioned entries).
    /// </summary>
    /// <param name="armType">The ARM type, for example <c>Microsoft.App/containerApps</c>.</param>
    /// <param name="mapping">The mapping to use.</param>
    /// <param name="apiVersion">Optional API version the override is pinned to.</param>
    public static void AddOverride(string armType, AzureNativeTypeMapping mapping, string? apiVersion = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(armType);
        ArgumentNullException.ThrowIfNull(mapping);
        Overrides[apiVersion is null ? armType : $"{armType}@{apiVersion}"] = mapping;
    }

    /// <summary>
    /// Maps an ARM resource type to its azure-native token and argument names.
    /// </summary>
    /// <param name="armType">The ARM type, for example <c>Microsoft.App/containerApps</c>.</param>
    /// <param name="apiVersion">The API version Aspire emitted, used for exact-version override pins.</param>
    /// <param name="resourceNameLiteral">
    /// The literal resource name when known; required to disambiguate config-singleton families such as
    /// <c>Microsoft.Web/sites/config</c> where the child name selects the azure-native type.
    /// </param>
    public static AzureNativeTypeMapping Map(string armType, string? apiVersion = null, string? resourceNameLiteral = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(armType);

        // Exact-version pin first, then the config-singleton dispatch, then the unversioned override.
        if (apiVersion is not null && Overrides.TryGetValue($"{armType}@{apiVersion}", out var pinned))
        {
            return pinned;
        }

        if (armType.EndsWith("/config", StringComparison.OrdinalIgnoreCase))
        {
            if (resourceNameLiteral is null || !Overrides.TryGetValue($"{armType}:{resourceNameLiteral}", out var configMapping))
            {
                throw new AzureProvisioningTranslationException(
                    $"The ARM config-singleton type '{armType}' with name '{resourceNameLiteral ?? "<non-literal>"}' has no " +
                    "azure-native mapping. Config child resources map to name-specific azure-native types; add an explicit " +
                    $"mapping via {nameof(AzureNativeTypeCatalog)}.{nameof(AddOverride)}(\"{armType}:{resourceNameLiteral}\", ...).");
            }

            return configMapping;
        }

        if (Overrides.TryGetValue(armType, out var mapping))
        {
            return mapping;
        }

        return MapByConvention(armType);
    }

    /// <summary>Gets the invoke token used to translate a <c>listKeys()</c> call on the given ARM type.</summary>
    /// <param name="armType">The ARM type of the resource the template calls <c>listKeys()</c> on.</param>
    public static string? GetListKeysInvokeToken(string armType) =>
        ListKeysInvokes.TryGetValue(armType, out var token) ? token : null;

    private static AzureNativeTypeMapping MapByConvention(string armType)
    {
        var segments = armType.Split('/');
        if (segments.Length < 2 || !segments[0].StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase))
        {
            throw new AzureProvisioningTranslationException(
                $"The ARM resource type '{armType}' cannot be mapped to an azure-native token by convention. " +
                $"Add an explicit mapping via {nameof(AzureNativeTypeCatalog)}.{nameof(AddOverride)}.");
        }

        if (segments.Length > 2)
        {
            // Child types need a parent-link argument name that cannot be derived reliably; require an override.
            throw new AzureProvisioningTranslationException(
                $"The child ARM resource type '{armType}' has no azure-native mapping. Child resources need an explicit " +
                $"parent argument mapping; add one via {nameof(AzureNativeTypeCatalog)}.{nameof(AddOverride)}.");
        }

        var module = segments[0]["Microsoft.".Length..].ToLowerInvariant();
        var typeName = ToSingularPascal(segments[1]);

        return new AzureNativeTypeMapping
        {
            Token = $"azure-native:{module}:{typeName}",
            NameArgName = char.ToLowerInvariant(typeName[0]) + typeName[1..] + "Name",
            GetInvokeToken = $"azure-native:{module}:get{typeName}",
        };
    }

    private static string ToSingularPascal(string plural)
    {
        var singular = plural switch
        {
            _ when plural.EndsWith("ies", StringComparison.Ordinal) => plural[..^3] + "y",
            _ when plural.EndsWith("ses", StringComparison.Ordinal) => plural[..^2],
            _ when plural.EndsWith("s", StringComparison.Ordinal) => plural[..^1],
            _ => plural,
        };

        return char.ToUpperInvariant(singular[0]) + singular[1..];
    }
}
