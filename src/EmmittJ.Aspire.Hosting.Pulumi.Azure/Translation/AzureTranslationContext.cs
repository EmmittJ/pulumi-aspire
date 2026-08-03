// Licensed under the MIT License.

using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Pulumi;

namespace EmmittJ.Aspire.Hosting.Pulumi.Azure;

/// <summary>
/// Shared state for one Pulumi program run that translates the Azure provisioning model of the
/// application into Pulumi azure-native resources: the values of the native provisioning context
/// (subscription, tenant, resource group, location, principal) and the templates translated so far
/// (used to wire environment outputs into deployment-target parameters in-memory instead of through
/// string round-trips).
/// </summary>
public sealed class AzureTranslationContext
{
    private readonly Dictionary<global::Aspire.Hosting.Azure.AzureBicepResource, TranslatedAzureTemplate> _templates = new();

    internal AzureTranslationContext(
        Output<string> subscriptionId,
        Output<string> tenantId,
        Output<string> resourceGroupName,
        Output<string> location,
        Output<string> principalId,
        Output<string> principalName,
        ILogger logger)
    {
        SubscriptionId = subscriptionId;
        TenantId = tenantId;
        ResourceGroupName = resourceGroupName;
        Location = location;
        PrincipalId = principalId;
        PrincipalName = principalName;
        Logger = logger;
    }

    /// <summary>Gets the subscription id from the native provisioning context.</summary>
    public Output<string> SubscriptionId { get; }

    /// <summary>Gets the tenant id from the native provisioning context (empty when unknown).</summary>
    public Output<string> TenantId { get; }

    /// <summary>Gets the name of the resource group the native provisioning context targets.</summary>
    public Output<string> ResourceGroupName { get; }

    /// <summary>Gets the Azure location of the native provisioning context.</summary>
    public Output<string> Location { get; }

    /// <summary>Gets the object id of the deploying principal.</summary>
    public Output<string> PrincipalId { get; }

    /// <summary>Gets the name of the deploying principal.</summary>
    public Output<string> PrincipalName { get; }

    /// <summary>Gets the logger.</summary>
    public ILogger Logger { get; }

    /// <summary>Gets the per-resource customization hook invoked before each translated resource is created.</summary>
    public Action<AzureNativeResourceCustomizationContext>? ConfigureResource { get; init; }

    /// <summary>Gets the templates translated so far, keyed by the source Aspire resource.</summary>
    public IReadOnlyDictionary<global::Aspire.Hosting.Azure.AzureBicepResource, TranslatedAzureTemplate> Templates => _templates;

    internal void RegisterTemplate(global::Aspire.Hosting.Azure.AzureBicepResource resource, TranslatedAzureTemplate template) =>
        _templates[resource] = template;
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
