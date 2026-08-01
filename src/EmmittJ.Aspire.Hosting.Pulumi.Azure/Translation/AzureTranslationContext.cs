// Licensed under the MIT License.

using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Pulumi;

namespace EmmittJ.Aspire.Hosting.Pulumi.Azure;

/// <summary>
/// Shared state for translating the Azure provisioning model of one adopted environment into Pulumi
/// azure-native resources inside a single Pulumi program: the target resource group, ambient Azure client
/// configuration, the value resolver for Aspire parameter values, and the templates translated so far
/// (used to wire environment outputs into deployment-target parameters in-memory instead of through
/// string round-trips).
/// </summary>
public sealed class AzureTranslationContext
{
    private readonly Dictionary<global::Aspire.Hosting.Azure.AzureBicepResource, TranslatedAzureTemplate> _templates = new();
    private Output<global::Pulumi.AzureNative.Authorization.GetClientConfigResult>? _clientConfig;

    internal AzureTranslationContext(
        Output<string> resourceGroupName,
        Output<string> location,
        PulumiValueResolver valueResolver,
        ILogger logger,
        bool useDeterministicPlaceholders)
    {
        ResourceGroupName = resourceGroupName;
        Location = location;
        ValueResolver = valueResolver;
        Logger = logger;
        UseDeterministicPlaceholders = useDeterministicPlaceholders;
    }

    /// <summary>Gets the name of the resource group every translated resource targets.</summary>
    public Output<string> ResourceGroupName { get; }

    /// <summary>Gets the Azure location used for the resource group and location-defaulted resources.</summary>
    public Output<string> Location { get; }

    /// <summary>Gets the resolver for Aspire structured parameter values.</summary>
    public PulumiValueResolver ValueResolver { get; }

    /// <summary>Gets the logger.</summary>
    public ILogger Logger { get; }

    /// <summary>
    /// Gets whether ambient-credential invokes (client config, resource property reads) are replaced with
    /// deterministic placeholder values. Enabled for publish previews, which must produce an artifact
    /// without Azure credentials; deploys always use real invokes.
    /// </summary>
    public bool UseDeterministicPlaceholders { get; }

    /// <summary>Gets the per-resource customization hook invoked before each translated resource is created.</summary>
    public Action<AzureNativeResourceCustomizationContext>? ConfigureResource { get; init; }

    /// <summary>Gets the templates translated so far, keyed by the source Aspire resource.</summary>
    public IReadOnlyDictionary<global::Aspire.Hosting.Azure.AzureBicepResource, TranslatedAzureTemplate> Templates => _templates;

    /// <summary>Gets the ambient subscription id (placeholder-substituted for previews).</summary>
    public Output<string> SubscriptionId => GetClientConfigValue(static r => r.SubscriptionId, "subscriptionId");

    /// <summary>Gets the ambient tenant id (placeholder-substituted for previews).</summary>
    public Output<string> TenantId => GetClientConfigValue(static r => r.TenantId, "tenantId");

    /// <summary>Gets the object id of the deploying principal (placeholder-substituted for previews).</summary>
    public Output<string> PrincipalId => GetClientConfigValue(static r => r.ObjectId, "principalId");

    /// <summary>Gets the ARM id of the target resource group.</summary>
    public Output<string> ResourceGroupId =>
        Output.Format($"/subscriptions/{SubscriptionId}/resourceGroups/{ResourceGroupName}");

    internal void RegisterTemplate(global::Aspire.Hosting.Azure.AzureBicepResource resource, TranslatedAzureTemplate template) =>
        _templates[resource] = template;

    private Output<string> GetClientConfigValue(
        Func<global::Pulumi.AzureNative.Authorization.GetClientConfigResult, string> selector,
        string placeholderName)
    {
        if (UseDeterministicPlaceholders)
        {
            return Output.Create($"<preview:{placeholderName}>");
        }

        _clientConfig ??= global::Pulumi.AzureNative.Authorization.GetClientConfig.Invoke();
        return _clientConfig.Apply(selector);
    }
}

/// <summary>
/// The Pulumi-side result of translating one Aspire Azure template (an environment resource or a
/// deployment target): the component resource grouping the translated resources, the resources themselves,
/// and the template outputs as live Pulumi outputs under the exact names Aspire's
/// <c>BicepOutputReference</c>s expect.
/// </summary>
public sealed class TranslatedAzureTemplate
{
    internal TranslatedAzureTemplate(
        string name,
        ComponentResource component,
        IReadOnlyList<global::Pulumi.Resource> resources,
        IReadOnlyDictionary<string, Output<string>> outputs)
    {
        Name = name;
        Component = component;
        Resources = resources;
        Outputs = outputs;
    }

    /// <summary>Gets the Aspire resource name the template came from.</summary>
    public string Name { get; }

    /// <summary>Gets the component resource grouping the translated resources for readable previews.</summary>
    public ComponentResource Component { get; }

    /// <summary>Gets the translated Pulumi resources.</summary>
    public IReadOnlyList<global::Pulumi.Resource> Resources { get; }

    /// <summary>Gets the template outputs, keyed by the template's output names.</summary>
    public IReadOnlyDictionary<string, Output<string>> Outputs { get; }
}

/// <summary>
/// The post-translation customization surface: lets callers tweak the raw property bag and resource options
/// of each translated resource just before it is created (Pulumi-only concerns such as providers, aliases,
/// protect flags, or property overrides).
/// </summary>
public sealed class AzureNativeResourceCustomizationContext
{
    internal AzureNativeResourceCustomizationContext(
        string templateName,
        string bicepIdentifier,
        string armType,
        string? apiVersion,
        string token,
        IDictionary<string, object?> properties,
        CustomResourceOptions options)
    {
        TemplateName = templateName;
        BicepIdentifier = bicepIdentifier;
        ArmType = armType;
        ApiVersion = apiVersion;
        Token = token;
        Properties = properties;
        Options = options;
    }

    /// <summary>Gets the Aspire resource name of the template the resource belongs to.</summary>
    public string TemplateName { get; }

    /// <summary>Gets the resource's bicep identifier inside the template.</summary>
    public string BicepIdentifier { get; }

    /// <summary>Gets the ARM resource type, for example <c>Microsoft.App/containerApps</c>.</summary>
    public string ArmType { get; }

    /// <summary>Gets the ARM API version Aspire emitted for the resource.</summary>
    public string? ApiVersion { get; }

    /// <summary>Gets the azure-native token the resource maps to.</summary>
    public string Token { get; }

    /// <summary>Gets the mutable property bag the resource will be created with.</summary>
    public IDictionary<string, object?> Properties { get; }

    /// <summary>Gets the mutable resource options the resource will be created with.</summary>
    public CustomResourceOptions Options { get; }
}
