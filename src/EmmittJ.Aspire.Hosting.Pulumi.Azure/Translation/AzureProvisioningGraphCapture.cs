// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Azure.Provisioning;
using Azure.Provisioning.Primitives;

namespace EmmittJ.Aspire.Hosting.Pulumi.Azure;

/// <summary>
/// Captures the Azure.Provisioning construct graph an <see cref="AzureProvisioningResource"/> materializes,
/// replicating the exact sequence <c>GetBicepTemplateFile()</c> performs so the captured graph is
/// byte-identical to the compiled template (see <c>docs/spikes/azure-provisioning-translation-source.md</c>).
/// </summary>
/// <remarks>
/// The sequence is load-bearing and pinned by the fixture tests:
/// <list type="number">
/// <item>construct the internal <see cref="AzureResourceInfrastructure"/> (via an unsafe accessor — the type is sealed with an internal constructor),</item>
/// <item>run the resource's <see cref="AzureProvisioningResource.ConfigureInfrastructure"/> callback,</item>
/// <item>replicate the private <c>EnsureParametersAlign</c> (declares a string parameter for every <c>Parameters</c> entry the infrastructure doesn't declare itself, and back-fills known-parameter entries),</item>
/// <item><see cref="Infrastructure.Build"/> <b>before</b> walking — Build mutates constructs in-place, most importantly assigning default resource names.</item>
/// </list>
/// </remarks>
internal static class AzureProvisioningGraphCapture
{
    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    private static extern AzureResourceInfrastructure CreateInfrastructure(AzureProvisioningResource resource, string name);

    /// <summary>
    /// Builds and returns the fully configured, built construct graph for the given provisioning resource.
    /// </summary>
    /// <param name="resource">The Aspire Azure provisioning resource (environment or deployment target).</param>
    public static AzureResourceInfrastructure Capture(AzureProvisioningResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var infrastructure = CreateInfrastructure(resource, resource.Name);
        resource.ConfigureInfrastructure(infrastructure);
        EnsureParametersAlign(resource, infrastructure);

        // Build() must run before the graph is walked: it assigns default names to unnamed resources and
        // finalizes construct state in-place.
        infrastructure.Build(resource.ProvisioningBuildOptions);
        return infrastructure;
    }

    // Replicates AzureProvisioningResource.EnsureParametersAlign (private in Aspire.Hosting.Azure 13.4.6).
    private static void EnsureParametersAlign(AzureProvisioningResource resource, AzureResourceInfrastructure infrastructure)
    {
        var declared = infrastructure.GetProvisionableResources()
            .OfType<ProvisioningParameter>()
            .DistinctBy(p => p.BicepIdentifier)
            .ToDictionary(p => p.BicepIdentifier);

        foreach (var (key, value) in resource.Parameters)
        {
            if (!declared.ContainsKey(key))
            {
                var isSecure = value is ParameterResource { Secret: true };
                infrastructure.Add(new ProvisioningParameter(key, typeof(string)) { IsSecure = isSecure });
            }
        }

        foreach (var (identifier, _) in declared)
        {
            if (IsKnownParameterName(identifier) && identifier != AzureBicepResource.KnownParameters.Location)
            {
                resource.Parameters.TryAdd(identifier, null);
            }
        }
    }

    // Replicates the internal AzureBicepResource.KnownParameters.IsKnownParameterName (pinned by fixture tests).
    private static bool IsKnownParameterName(string name) => name is "principalName" or "principalType"
        or "principalId" or "userPrincipalId" or "keyVaultName" or "location" or "logAnalyticsWorkspaceId";
}
