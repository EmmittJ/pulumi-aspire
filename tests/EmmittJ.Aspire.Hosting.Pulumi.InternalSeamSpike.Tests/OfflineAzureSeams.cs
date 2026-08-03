// Licensed under the MIT License.

using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.Provisioning;
using Aspire.Hosting.Azure.Provisioning.Internal;
using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;

namespace EmmittJ.Aspire.Hosting.Pulumi.InternalSeamSpike.Tests;

/// <summary>
/// Offline implementations of the seams the default Azure environment resolves from DI at step-execution
/// time. Everything here compiles directly against Aspire.Hosting.Azure's <b>internal</b> interfaces —
/// see the csproj for the IVT/public-sign trick that makes that possible.
/// </summary>
/// <remarks>
/// Seam inventory (all registered by <c>AddAzureProvisioning</c> via <c>TryAddSingleton</c>, so all
/// replaceable with <c>Services.Replace</c>):
/// <list type="bullet">
/// <item><see cref="ITokenCredentialProvider"/> (public) — feeds <c>validate-azure-login</c> and ACR login.</item>
/// <item><see cref="IAcrLoginService"/> (internal) — the <c>login-to-acr-*</c> step body.</item>
/// <item><see cref="IUserPrincipalProvider"/> (internal) — principal for role-assignment parameters.</item>
/// <item><see cref="IArmClientProvider"/> (internal) — subscription/tenant/resource-group graph used by
/// <c>create-provisioning-context</c> and <c>destroy-azure-*</c>.</item>
/// <item><see cref="IBicepProvisioner"/> (internal) — the per-resource body of
/// <c>provision-azure-bicep-resources</c>; the Pulumi execution seam.</item>
/// </list>
/// </remarks>
internal static class OfflineAzureSeams
{
    internal sealed class SpikeTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("spike-token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    internal sealed class SpikeTokenCredentialProvider : ITokenCredentialProvider
    {
        public TokenCredential TokenCredential { get; } = new SpikeTokenCredential();
    }

    internal sealed class SpikeUserPrincipalProvider : IUserPrincipalProvider
    {
        public static readonly Guid PrincipalId = new("11111111-2222-3333-4444-555555555555");

        public Task<UserPrincipal> GetUserPrincipalAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new UserPrincipal(PrincipalId, "spike@example.dev"));
    }

    internal sealed class SpikeAcrLoginService : IAcrLoginService
    {
        public List<string> Logins { get; } = [];

        public Task LoginAsync(string registryEndpoint, string tenantId, TokenCredential credential, CancellationToken cancellationToken = default)
        {
            Logins.Add(registryEndpoint);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Offline ARM client graph. Resource groups are held in-memory; deployments are never issued from
    /// here because the <see cref="IBicepProvisioner"/> seam intercepts above the ARM boundary.
    /// </summary>
    internal sealed class SpikeArmClientProvider : IArmClientProvider
    {
        public static readonly Guid TenantId = new("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        public Dictionary<string, SpikeResourceGroup> ResourceGroups { get; } = [];

        public List<string> DeletedResourceGroups { get; } = [];

        public IArmClient GetArmClient(TokenCredential credential, string subscriptionId) => new SpikeArmClient(this, subscriptionId);

        public IArmClient GetArmClient(TokenCredential credential) => new SpikeArmClient(this, Guid.Empty.ToString());
    }

    internal sealed class SpikeArmClient(SpikeArmClientProvider owner, string subscriptionId) : IArmClient
    {
        public Task<(ISubscriptionResource subscription, ITenantResource tenant)> GetSubscriptionAndTenantAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<(ISubscriptionResource, ITenantResource)>(
                (new SpikeSubscription(owner, subscriptionId), new SpikeTenant()));

        public Task<IEnumerable<ITenantResource>> GetAvailableTenantsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IEnumerable<ITenantResource>>([new SpikeTenant()]);

        public Task<IEnumerable<ISubscriptionResource>> GetAvailableSubscriptionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IEnumerable<ISubscriptionResource>>([new SpikeSubscription(owner, subscriptionId)]);

        public Task<IEnumerable<ISubscriptionResource>> GetAvailableSubscriptionsAsync(string? tenantId, CancellationToken cancellationToken = default) =>
            GetAvailableSubscriptionsAsync(cancellationToken);

        public Task<IEnumerable<(string Name, string DisplayName)>> GetAvailableLocationsAsync(string subscriptionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IEnumerable<(string, string)>>([("westus2", "West US 2")]);

        public Task<IEnumerable<(string Name, string Location)>> GetAvailableResourceGroupsWithLocationAsync(string subscriptionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(owner.ResourceGroups.Values.Select(g => (g.Name, "westus2")));

        public IRoleAssignmentCollection GetRoleAssignments(ResourceIdentifier scope) =>
            throw new NotSupportedException("Role assignments are provisioned through the bicep templates in publish mode.");
    }

    internal sealed class SpikeSubscription(SpikeArmClientProvider owner, string subscriptionId) : ISubscriptionResource
    {
        public ResourceIdentifier Id { get; } = new($"/subscriptions/{subscriptionId}");

        public string? DisplayName => "Spike Subscription";

        public Guid? TenantId => SpikeArmClientProvider.TenantId;

        public IResourceGroupCollection GetResourceGroups() => new SpikeResourceGroupCollection(owner, subscriptionId);

        public IArmDeploymentCollection GetArmDeployments() =>
            throw new NotSupportedException("Subscription-scoped ARM deployments are intercepted at the IBicepProvisioner seam.");
    }

    internal sealed class SpikeTenant : ITenantResource
    {
        public Guid? TenantId => SpikeArmClientProvider.TenantId;

        public string? DisplayName => "Spike Tenant";

        public string? DefaultDomain => "spike.example.dev";
    }

    internal sealed class SpikeResourceGroupCollection(SpikeArmClientProvider owner, string subscriptionId) : IResourceGroupCollection
    {
        public Task<Response<IResourceGroupResource>> GetAsync(string resourceGroupName, CancellationToken cancellationToken = default)
        {
            if (owner.ResourceGroups.TryGetValue(resourceGroupName, out var group))
            {
                return Task.FromResult(Response.FromValue<IResourceGroupResource>(group, new SpikeRawResponse()));
            }

            throw new RequestFailedException(404, $"Resource group '{resourceGroupName}' not found.");
        }

        public Task<ArmOperation<IResourceGroupResource>> CreateOrUpdateAsync(WaitUntil waitUntil, string resourceGroupName, ResourceGroupData data, CancellationToken cancellationToken = default)
        {
            var group = new SpikeResourceGroup(owner, subscriptionId, resourceGroupName);
            owner.ResourceGroups[resourceGroupName] = group;
            return Task.FromResult<ArmOperation<IResourceGroupResource>>(new SpikeArmOperation<IResourceGroupResource>(group));
        }
    }

    internal sealed class SpikeResourceGroup(SpikeArmClientProvider owner, string subscriptionId, string name) : IResourceGroupResource
    {
        public ResourceIdentifier Id { get; } = new($"/subscriptions/{subscriptionId}/resourceGroups/{name}");

        public string Name => name;

        public IArmDeploymentCollection GetArmDeployments() =>
            throw new NotSupportedException("Resource-group ARM deployments are intercepted at the IBicepProvisioner seam.");

        public Task DeleteAsync(WaitUntil waitUntil, CancellationToken cancellationToken = default)
        {
            owner.DeletedResourceGroups.Add(name);
            owner.ResourceGroups.Remove(name);
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<(string Name, string ResourceType)> GetResourcesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    internal sealed class SpikeArmOperation<T>(T value) : ArmOperation<T>
    {
        public override T Value => value;

        public override bool HasValue => true;

        public override string Id => "spike-operation";

        public override bool HasCompleted => true;

        public override Response GetRawResponse() => new SpikeRawResponse();

        public override Response UpdateStatus(CancellationToken cancellationToken = default) => GetRawResponse();

        public override ValueTask<Response> UpdateStatusAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(GetRawResponse());
    }

    internal sealed class SpikeRawResponse : Response
    {
        public override int Status => 200;

        public override string ReasonPhrase => "OK";

        public override Stream? ContentStream { get; set; }

        public override string ClientRequestId { get; set; } = "spike";

        public override void Dispose()
        {
        }

        protected override bool ContainsHeader(string name) => false;

        protected override IEnumerable<HttpHeader> EnumerateHeaders() => [];

        protected override bool TryGetHeader(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value)
        {
            value = null;
            return false;
        }

        protected override bool TryGetHeaderValues(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IEnumerable<string>? values)
        {
            values = null;
            return false;
        }
    }
}
