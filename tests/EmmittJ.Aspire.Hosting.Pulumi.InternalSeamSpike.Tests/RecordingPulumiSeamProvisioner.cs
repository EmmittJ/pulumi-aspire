// Licensed under the MIT License.

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.Provisioning;
using Microsoft.Extensions.Configuration;

namespace EmmittJ.Aspire.Hosting.Pulumi.InternalSeamSpike.Tests;

/// <summary>
/// The Pulumi execution seam: an <see cref="IBicepProvisioner"/> that replaces only the innermost
/// "deploy this template to ARM" call while reusing everything else the default Azure environment does —
/// login validation, provisioning-context creation, per-resource provision steps (ordering, reporting UI,
/// state), <b>and Aspire's own parameter/scope resolution</b> (<see cref="BicepUtilities"/>), which the
/// production integration currently re-implements in <c>PulumiValueResolver</c>.
/// </summary>
/// <remarks>
/// The spike records each provisioned template and back-propagates deterministic placeholder outputs into
/// <see cref="AzureBicepResource.Outputs"/> — exactly the contract the real <c>BicepProvisioner</c>
/// honors, and what makes later templates' <c>BicepOutputReference</c> parameters resolve. A production
/// implementation would instead run the template through the existing Bicep→azure-native translation core
/// inside a Pulumi stack and back-propagate the real stack outputs.
/// </remarks>
internal sealed partial class RecordingPulumiSeamProvisioner : IBicepProvisioner
{
    internal sealed record ProvisionedTemplate(
        string Name,
        JsonObject Parameters,
        JsonObject Scope,
        IReadOnlyList<string> Outputs);

    public List<ProvisionedTemplate> Provisioned { get; } = [];

    public Task<bool> ConfigureResourceAsync(IConfiguration configuration, AzureBicepResource resource, CancellationToken cancellationToken) =>
        Task.FromResult(false); // never reuse prior deployment state — always hand the template to Pulumi

    public async Task GetOrCreateResourceAsync(AzureBicepResource resource, ProvisioningContext context, CancellationToken cancellationToken)
    {
        // Reuse the same well-known parameter population the real BicepProvisioner performs (principal +
        // location come from the provisioning context the native create-provisioning-context step built).
        PopulateWellKnownParameters(resource, context);

        // Reuse Aspire's own parameter/scope resolution verbatim — this is the logic PulumiValueResolver
        // re-implements today.
        var parameters = new JsonObject();
        await BicepUtilities.SetParametersAsync(parameters, resource, skipKnownValues: false, cancellationToken).ConfigureAwait(false);
        var scope = new JsonObject();
        await BicepUtilities.SetScopeAsync(scope, resource, cancellationToken).ConfigureAwait(false);

        // Back-propagate outputs so later templates' BicepOutputReference parameters resolve — the same
        // contract the real provisioner honors from the ARM deployment result. The spike substitutes
        // deterministic ARM-resource-id-shaped placeholders (native steps such as the ACA portal-link
        // parse outputs with Azure.Core.ResourceIdentifier); production substitutes Pulumi stack outputs.
        var outputs = new List<string>();
        foreach (Match match in OutputDeclaration().Matches(resource.GetBicepTemplateString()))
        {
            var name = match.Groups[1].Value;
            outputs.Add(name);
            resource.Outputs[name] = PlaceholderOutput(context, resource.Name, name);
        }

        Provisioned.Add(new(resource.Name, parameters, scope, outputs));
    }

    private static void PopulateWellKnownParameters(AzureBicepResource resource, ProvisioningContext context)
    {
        if (resource.Parameters.TryGetValue(AzureBicepResource.KnownParameters.PrincipalId, out var principalId) && principalId is null)
        {
            resource.Parameters[AzureBicepResource.KnownParameters.PrincipalId] = context.Principal.Id;
        }

        if (resource.Parameters.TryGetValue(AzureBicepResource.KnownParameters.PrincipalName, out var principalName) && principalName is null)
        {
            resource.Parameters[AzureBicepResource.KnownParameters.PrincipalName] = context.Principal.Name;
        }

        if (resource.Parameters.TryGetValue(AzureBicepResource.KnownParameters.PrincipalType, out var principalType) && principalType is null)
        {
            resource.Parameters[AzureBicepResource.KnownParameters.PrincipalType] = "User";
        }

        resource.Parameters[AzureBicepResource.KnownParameters.Location] = context.Location.Name;
    }

    private static string PlaceholderOutput(ProvisioningContext context, string resourceName, string outputName) =>
        $"/subscriptions/{context.Subscription.Id.SubscriptionId}/resourceGroups/{context.ResourceGroup.Name}/providers/Microsoft.Spike/{resourceName}/{outputName}";

    [GeneratedRegex(@"^output\s+(\w+)\s", RegexOptions.Multiline)]
    private static partial Regex OutputDeclaration();
}
