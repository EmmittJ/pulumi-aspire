// Licensed under the MIT License.

using System.Text.Json.Nodes;
using Aspire.Hosting.Azure;

namespace EmmittJ.Aspire.Hosting.Pulumi.Azure.Seams;

/// <summary>
/// One Azure template the native Aspire provisioning pipeline handed to the execution seam: the Aspire
/// resource carrying the template, its parameters fully resolved by Aspire's own internal
/// <c>BicepUtilities</c> (ARM deployment-parameter format, <c>{ "name": { "value": ... } }</c>), the
/// deployment scope, and the provisioning-context values the native <c>create-provisioning-context</c> step
/// established. Every type on this surface is public — the internal-seam access is confined to the assembly
/// that produced this object.
/// </summary>
public sealed class AzureTemplateDeployment
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AzureTemplateDeployment"/> class.
    /// </summary>
    /// <param name="resource">The Aspire resource carrying the template.</param>
    /// <param name="parameters">The resolved template parameters in ARM deployment-parameter format.</param>
    /// <param name="scope">The deployment scope (<c>{ "resourceGroup": ... }</c>).</param>
    /// <param name="context">The provisioning-context values for the deployment.</param>
    public AzureTemplateDeployment(
        AzureBicepResource resource,
        JsonObject parameters,
        JsonObject scope,
        AzureDeploymentContext context)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(context);

        Resource = resource;
        Parameters = parameters;
        Scope = scope;
        Context = context;
    }

    /// <summary>Gets the Aspire resource carrying the template (an environment resource, its supporting
    /// resources, or a compute resource's deployment target).</summary>
    public AzureBicepResource Resource { get; }

    /// <summary>
    /// Gets the template parameters, resolved by Aspire's own parameter resolution
    /// (<see cref="global::Aspire.Hosting.ApplicationModel.ReferenceExpression"/>s,
    /// <c>BicepOutputReference</c>s, <c>ParameterResource</c>s, ...) into ARM deployment-parameter format:
    /// <c>{ "parameterName": { "value": &lt;json&gt; } }</c>.
    /// </summary>
    public JsonObject Parameters { get; }

    /// <summary>Gets the deployment scope (<c>{ "resourceGroup": &lt;name-or-null&gt; }</c>). Non-null when
    /// the resource targets an existing resource group other than the provisioning context's.</summary>
    public JsonObject Scope { get; }

    /// <summary>Gets the provisioning-context values for the deployment.</summary>
    public AzureDeploymentContext Context { get; }

    /// <summary>
    /// Gets the name of the resource group this template deploys into: the scope's resource group when the
    /// resource declares one, otherwise the provisioning context's resource group.
    /// </summary>
    public string ResourceGroupName =>
        Scope["resourceGroup"]?.GetValue<string>() ?? Context.ResourceGroupName;
}

/// <summary>
/// The provisioning-context values the native <c>create-provisioning-context</c> step established, extracted
/// to public types: subscription, tenant, resource group, location, and the deploying principal.
/// </summary>
public sealed class AzureDeploymentContext
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AzureDeploymentContext"/> class.
    /// </summary>
    /// <param name="subscriptionId">The target subscription id.</param>
    /// <param name="tenantId">The Entra tenant id, when known.</param>
    /// <param name="resourceGroupName">The default target resource group name.</param>
    /// <param name="location">The Azure location name.</param>
    /// <param name="principalId">The deploying principal's object id.</param>
    /// <param name="principalName">The deploying principal's name.</param>
    public AzureDeploymentContext(
        string subscriptionId,
        string? tenantId,
        string resourceGroupName,
        string location,
        string principalId,
        string principalName)
    {
        SubscriptionId = subscriptionId;
        TenantId = tenantId;
        ResourceGroupName = resourceGroupName;
        Location = location;
        PrincipalId = principalId;
        PrincipalName = principalName;
    }

    /// <summary>Gets the target subscription id.</summary>
    public string SubscriptionId { get; }

    /// <summary>Gets the Entra tenant id, when known.</summary>
    public string? TenantId { get; }

    /// <summary>Gets the default target resource group name (created by the native
    /// <c>create-provisioning-context</c> step).</summary>
    public string ResourceGroupName { get; }

    /// <summary>Gets the Azure location name.</summary>
    public string Location { get; }

    /// <summary>Gets the deploying principal's object id.</summary>
    public string PrincipalId { get; }

    /// <summary>Gets the deploying principal's name.</summary>
    public string PrincipalName { get; }
}

/// <summary>
/// The execution engine plugged into Aspire's native Azure provisioning pipeline. Every Bicep template the
/// native environment materializes — the compute environment, its supporting resources (container registry,
/// Key Vault, ...), and each compute resource's deployment target — flows through
/// <see cref="ProvisionAsync"/> with parameters already resolved, while the native steps keep their
/// ordering, reporting UI, and state handling.
/// </summary>
/// <remarks>
/// Implementations must honor one contract: <b>back-propagate the deployed template outputs into
/// <see cref="AzureBicepResource.Outputs"/></b> (and complete
/// <see cref="AzureBicepResource.ProvisioningTaskCompletionSource"/> when set) before returning, so later
/// templates' <c>BicepOutputReference</c> parameters — and native follow-up steps such as the container
/// registry login — resolve. Outputs whose names end in <c>_ID</c> are parsed by native steps with
/// <c>Azure.Core.ResourceIdentifier</c> and must be valid ARM resource ids.
/// </remarks>
public interface IAzureTemplateProvisioner
{
    /// <summary>Provisions one resolved Azure template.</summary>
    /// <param name="deployment">The resolved template deployment.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task ProvisionAsync(AzureTemplateDeployment deployment, CancellationToken cancellationToken);
}
