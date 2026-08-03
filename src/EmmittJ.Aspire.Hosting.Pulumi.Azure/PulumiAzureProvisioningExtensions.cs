// Licensed under the MIT License.

using Aspire.Hosting;
using EmmittJ.Aspire.Hosting.Pulumi.Azure;
using EmmittJ.Aspire.Hosting.Pulumi.Azure.Seams;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EmmittJ.Aspire.Hosting.Pulumi;

/// <summary>
/// Swaps the execution engine of Aspire's native Azure provisioning pipeline for Pulumi: the entire
/// pipeline — prepare, build, push, provisioning-context creation, per-resource provision ordering,
/// registry login, deployment-target deploys — runs unmodified, but every template that would be sent to
/// ARM is instead translated to Pulumi azure-native resources and deployed with <c>pulumi up</c> into a
/// real, independently usable Pulumi stack.
/// </summary>
public static class PulumiAzureProvisioningExtensions
{
    /// <summary>
    /// Deploys the application's Azure resources with Pulumi instead of ARM deployments when running
    /// <c>aspire deploy</c>. Use alongside a native Azure compute environment, for example
    /// <c>builder.AddAzureContainerAppEnvironment(...)</c>. Local development (<c>aspire run</c>) is not
    /// affected.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="configure">Optional Pulumi settings (project/stack names, customization hook).</param>
    /// <returns>The <paramref name="builder"/>, for chaining.</returns>
    /// <remarks>
    /// The subscription, resource group, and location come from Aspire's own provisioning flow
    /// (interactive prompts, <c>Azure:*</c> configuration, deployment-state), so <c>aspire deploy</c>
    /// looks and feels exactly like the default experience — Pulumi replaces only the execution engine,
    /// and the resulting stack supports <c>pulumi preview</c>, <c>pulumi destroy</c>, and drift detection
    /// out-of-band. Note that <c>aspire deploy</c>'s own destroy path deletes the resource group directly
    /// via ARM, which leaves the Pulumi stack's state stale; prefer <c>pulumi destroy</c> followed by
    /// <c>pulumi stack rm</c>, or refresh the stack afterwards.
    /// </remarks>
    public static IDistributedApplicationBuilder UsePulumiProvisioning(
        this IDistributedApplicationBuilder builder,
        Action<PulumiProvisioningOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.ExecutionContext.IsRunMode)
        {
            return builder;
        }

        var options = new PulumiProvisioningOptions();
        configure?.Invoke(options);

        builder.Services.TryAddSingleton<PulumiRunner>();
        AzureProvisioningSeams.UseTemplateProvisioner(
            builder.Services,
            serviceProvider => new PulumiTemplateProvisioner(options, serviceProvider));

        return builder;
    }
}
