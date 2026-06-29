// Licensed under the Apache License, Version 2.0.

using Aspire.Hosting.ApplicationModel;
using EmmittJ.Aspire.Hosting.Pulumi;
using Pulumi;

namespace EmmittJ.Aspire.Hosting.Pulumi.Azure.AppContainers;

/// <summary>
/// Per-deploy registry of the compute-resource contexts for a single Azure environment.
/// </summary>
/// <remarks>
/// <para>
/// This mirrors Aspire's <c>ContainerAppEnvironmentContext</c>: it caches one
/// <see cref="PulumiAzureContainerAppContext"/> per Aspire compute resource so that one resource can
/// resolve another resource's endpoint addressing (for example a reverse proxy referencing a frontend's URL).
/// </para>
/// <para>
/// The environment's managed-environment default domain is captured here as an <see cref="Output{T}"/> because
/// it is only known after the managed environment is provisioned. Endpoint URLs are composed lazily from it.
/// </para>
/// </remarks>
internal sealed class PulumiAzureContainerAppEnvironmentContext
{
    private readonly Dictionary<IResource, PulumiAzureContainerAppContext> _contexts = new(ResourceNameComparer.Instance);

    public PulumiAzureContainerAppEnvironmentContext(
        PulumiAzureContainerAppEnvironmentResource environment,
        PulumiPublishingContext publishingContext,
        Output<string> defaultDomain)
    {
        Environment = environment;
        PublishingContext = publishingContext;
        DefaultDomain = defaultDomain;
    }

    /// <summary>Gets the Azure environment resource being deployed.</summary>
    public PulumiAzureContainerAppEnvironmentResource Environment { get; }

    /// <summary>Gets the publishing context for the running Pulumi program.</summary>
    public PulumiPublishingContext PublishingContext { get; }

    /// <summary>Gets the managed environment's default domain (e.g. <c>happy-tree-1234.eastus.azurecontainerapps.io</c>).</summary>
    public Output<string> DefaultDomain { get; }

    /// <summary>
    /// Gets the context for the given compute resource, creating and caching it if necessary. The new
    /// context's endpoint mappings are computed eagerly so siblings can resolve its addressing.
    /// </summary>
    /// <param name="resource">The compute resource.</param>
    public PulumiAzureContainerAppContext GetOrCreateContext(IComputeResource resource)
    {
        if (!_contexts.TryGetValue(resource, out var context))
        {
            context = new PulumiAzureContainerAppContext(resource, this);
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
    public PulumiAzureContainerAppContext? TryGetContext(IResource resource) =>
        _contexts.TryGetValue(resource, out var context) ? context : null;
}
