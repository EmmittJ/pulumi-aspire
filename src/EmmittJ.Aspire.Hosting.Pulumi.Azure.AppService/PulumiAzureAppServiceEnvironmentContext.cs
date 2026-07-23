// Licensed under the MIT License.

using Aspire.Hosting.ApplicationModel;
using EmmittJ.Aspire.Hosting.Pulumi;
using Pulumi;

namespace EmmittJ.Aspire.Hosting.Pulumi.Azure.AppService;

/// <summary>
/// Per-deploy registry of the compute-resource contexts for a single Azure App Service environment.
/// </summary>
/// <remarks>
/// <para>
/// This mirrors the Container Apps provider's environment context: it caches one
/// <see cref="PulumiAzureAppServiceWebsiteContext"/> per Aspire compute resource so that one resource can
/// resolve another resource's endpoint addressing (for example a reverse proxy referencing a frontend's URL).
/// </para>
/// <para>
/// The shared site-name suffix is captured here as an <see cref="Output{T}"/> because it is generated inside
/// the Pulumi program (App Service hostnames are globally unique). Site hostnames are composed lazily from it,
/// which lets siblings compute each other's hostnames before any Web App resource is actually created.
/// </para>
/// </remarks>
internal sealed class PulumiAzureAppServiceEnvironmentContext
{
    private readonly Dictionary<IResource, PulumiAzureAppServiceWebsiteContext> _contexts = new(ResourceNameComparer.Instance);

    public PulumiAzureAppServiceEnvironmentContext(
        PulumiAzureAppServiceEnvironmentResource environment,
        PulumiPublishingContext publishingContext,
        Output<string> siteSuffix)
    {
        Environment = environment;
        PublishingContext = publishingContext;
        SiteSuffix = siteSuffix;
    }

    /// <summary>Gets the Azure App Service environment resource being deployed.</summary>
    public PulumiAzureAppServiceEnvironmentResource Environment { get; }

    /// <summary>Gets the publishing context for the running Pulumi program.</summary>
    public PulumiPublishingContext PublishingContext { get; }

    /// <summary>Gets the shared random suffix appended to every site name to make hostnames globally unique.</summary>
    public Output<string> SiteSuffix { get; }

    /// <summary>
    /// Gets the context for the given compute resource, creating and caching it if necessary. The new
    /// context's endpoint mappings are computed eagerly so siblings can resolve its addressing.
    /// </summary>
    /// <param name="resource">The compute resource.</param>
    public PulumiAzureAppServiceWebsiteContext GetOrCreateContext(IComputeResource resource)
    {
        if (!_contexts.TryGetValue(resource, out var context))
        {
            context = new PulumiAzureAppServiceWebsiteContext(resource, this);
            _contexts[resource] = context;
            context.EnsureEndpointsProcessed();
        }

        return context;
    }

    /// <summary>
    /// Gets the cached context for a resource, or <see langword="null"/> if the resource is not a compute
    /// resource targeted to this environment (for example a cross-environment reference).
    /// </summary>
    /// <param name="resource">The resource whose context to look up.</param>
    public PulumiAzureAppServiceWebsiteContext? TryGetContext(IResource resource) =>
        _contexts.TryGetValue(resource, out var context) ? context : null;
}
