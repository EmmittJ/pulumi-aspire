// Licensed under the MIT License.

using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.Provisioning;
using Aspire.Hosting.Azure.Provisioning.Internal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EmmittJ.Aspire.Hosting.Pulumi.Azure.Seams;

/// <summary>
/// The internal-seam boundary of the integration (see <c>docs/spikes/internal-seam-reuse.md</c>): swaps the
/// two execution seams of Aspire's default Azure provisioning pipeline so a pluggable
/// <see cref="IAzureTemplateProvisioner"/> becomes the execution engine while <b>every native step keeps
/// running unmodified</b> — prepare, publish wiring, login validation, provisioning-context creation
/// (subscription/resource-group selection), per-resource provision ordering and reporting, and the container
/// registry login.
/// </summary>
public static class AzureProvisioningSeams
{
    /// <summary>
    /// Replaces Aspire's internal <c>IBicepProvisioner</c> (the "deploy this template to ARM" seam that
    /// every native <c>provision-*</c> step resolves at execution time) with an adapter over
    /// <paramref name="provisionerFactory"/>, and replaces the internal <c>IBicepCompiler</c> with a no-op —
    /// the seam consumes the provisioning model, not compiled ARM JSON, so the <c>bicep</c> CLI is not
    /// required.
    /// </summary>
    /// <param name="services">The application's service collection.</param>
    /// <param name="provisionerFactory">Creates the execution engine the templates flow through.</param>
    /// <remarks>
    /// Aspire registers every provisioning service with <c>TryAddSingleton</c>, so this replacement is
    /// order-independent: calling it before or after <c>AddAzureContainerAppEnvironment</c> (or any other
    /// Azure integration) yields the same wiring.
    /// </remarks>
    public static void UseTemplateProvisioner(
        IServiceCollection services,
        Func<IServiceProvider, IAzureTemplateProvisioner> provisionerFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(provisionerFactory);

        services.Replace(ServiceDescriptor.Singleton<IBicepProvisioner>(
            serviceProvider => new SeamBicepProvisioner(provisionerFactory(serviceProvider), serviceProvider)));
        services.Replace(ServiceDescriptor.Singleton<IBicepCompiler>(new NoBicepCliCompiler()));
    }

    /// <summary>
    /// The adapter between Aspire's internal <c>IBicepProvisioner</c> contract and the public
    /// <see cref="IAzureTemplateProvisioner"/> surface. Mirrors the native <c>BicepProvisioner</c>'s
    /// pre-deployment work — well-known parameter population and Aspire's own parameter/scope resolution via
    /// the internal <c>BicepUtilities</c> — then hands the resolved template to the plugged-in engine.
    /// </summary>
    private sealed class SeamBicepProvisioner(
        IAzureTemplateProvisioner provisioner,
        IServiceProvider serviceProvider) : IBicepProvisioner
    {
        // Never reuse Aspire's deployment-state cache: the plugged-in engine (Pulumi) owns state and
        // idempotence, so every template is always handed through.
        public Task<bool> ConfigureResourceAsync(
            IConfiguration configuration,
            AzureBicepResource resource,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public async Task GetOrCreateResourceAsync(
            AzureBicepResource resource,
            ProvisioningContext context,
            CancellationToken cancellationToken)
        {
            PopulateWellKnownParameters(resource, context);

            // Reuse Aspire's own parameter/scope resolution verbatim: ReferenceExpressions,
            // BicepOutputReferences (resolved from the outputs earlier deployments back-propagated),
            // ParameterResources, and connection strings all resolve exactly as they do natively.
            var parameters = new JsonObject();
            await BicepUtilities.SetParametersAsync(parameters, resource, skipKnownValues: false, cancellationToken).ConfigureAwait(false);
            var scope = new JsonObject();
            await BicepUtilities.SetScopeAsync(scope, resource, cancellationToken).ConfigureAwait(false);

            var deployment = new AzureTemplateDeployment(
                resource,
                parameters,
                scope,
                new AzureDeploymentContext(
                    context.Subscription.Id.SubscriptionId
                        ?? throw new InvalidOperationException("The provisioning context's subscription has no subscription id."),
                    context.Tenant.TenantId?.ToString(),
                    context.ResourceGroup.Name,
                    context.Location.Name,
                    context.Principal.Id.ToString(),
                    context.Principal.Name));

            await provisioner.ProvisionAsync(deployment, cancellationToken).ConfigureAwait(false);

            // Key Vault resources resolve secrets through a live SecretClient; wire the resolver against
            // the vault URI output the engine back-propagated, exactly like the native provisioner does.
            if (resource is IAzureKeyVaultResource keyVault)
            {
                ConfigureSecretResolver(keyVault);
            }
        }

        private void ConfigureSecretResolver(IAzureKeyVaultResource keyVault)
        {
            var resource = (AzureBicepResource)keyVault;
            var vaultUri = resource.Outputs[keyVault.VaultUriOutputReference.Name] as string
                ?? throw new InvalidOperationException(
                    $"The provisioner did not back-propagate the '{keyVault.VaultUriOutputReference.Name}' output " +
                    $"for Key Vault resource '{resource.Name}', so its secret resolver cannot be configured.");

            var client = serviceProvider.GetRequiredService<ISecretClientProvider>().GetSecretClient(new Uri(vaultUri));
            keyVault.SecretResolver = async (secretReference, cancellationToken) =>
            {
                var secret = await client.GetSecretAsync(secretReference.SecretName, cancellationToken: cancellationToken).ConfigureAwait(false);
                return secret.Value.Value;
            };
        }

        /// <summary>Mirrors the native provisioner's well-known parameter population: principal values from
        /// the provisioning context for parameters declared-but-unvalued, and the context's location.</summary>
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
    }

    /// <summary>The bicep CLI is not needed: the plugged-in engine consumes the model, not ARM JSON.</summary>
    private sealed class NoBicepCliCompiler : IBicepCompiler
    {
        public Task<string> CompileBicepToArmAsync(string bicepFilePath, CancellationToken cancellationToken = default) =>
            Task.FromResult("{}");
    }
}
