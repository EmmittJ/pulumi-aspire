// Licensed under the MIT License.

#pragma warning disable ASPIRECOMPUTE001 // GetComputeResources / compute-resource APIs are experimental

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;
using Pulumi;
using PulumiResource = Pulumi.Resource;

namespace EmmittJ.Aspire.Hosting.Pulumi;

/// <summary>
/// State shared with provider packages while the Pulumi program runs for a deploy.
/// </summary>
/// <remarks>
/// A single instance is created per <c>pulumi up</c> and passed to
/// <see cref="PulumiEnvironmentResource.CreateStackResourcesAsync(PulumiPublishingContext)"/>. Providers
/// use it to enumerate the compute resources targeted to the environment, register the Pulumi resources
/// they create (so cross-resource references resolve), and export stack outputs.
/// </remarks>
public sealed class PulumiPublishingContext
{
    private readonly Dictionary<IResource, PulumiResource> _translated = new(ResourceNameComparer.Instance);
    private readonly Dictionary<string, Output<string>> _outputs = [];

    internal PulumiPublishingContext(
        DistributedApplicationModel model,
        PulumiEnvironmentResource environment,
        DistributedApplicationExecutionContext executionContext,
        IServiceProvider services,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        Model = model;
        Environment = environment;
        ExecutionContext = executionContext;
        Services = services;
        Logger = logger;
        CancellationToken = cancellationToken;
    }

    /// <summary>Gets the distributed application model.</summary>
    public DistributedApplicationModel Model { get; }

    /// <summary>Gets the Pulumi environment resource that owns this deploy.</summary>
    public PulumiEnvironmentResource Environment { get; }

    /// <summary>Gets the execution context (publish/deploy) for resolving callback values.</summary>
    public DistributedApplicationExecutionContext ExecutionContext { get; }

    /// <summary>Gets the service provider for the running pipeline step.</summary>
    public IServiceProvider Services { get; }

    /// <summary>Gets the logger.</summary>
    public ILogger Logger { get; }

    /// <summary>Gets the cancellation token for the deploy operation.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Gets the stack outputs registered so far, keyed by output name.</summary>
    public IReadOnlyDictionary<string, Output<string>> Outputs => _outputs;

    /// <summary>Gets the Pulumi resources translated so far, keyed by their source Aspire resource.</summary>
    public IReadOnlyDictionary<IResource, PulumiResource> TranslatedResources => _translated;

    /// <summary>
    /// Enumerates the compute resources that should be translated by this environment: resources that
    /// target this environment (or no specific environment) and are not opted out of translation.
    /// </summary>
    public IEnumerable<IComputeResource> GetTargetedComputeResources()
    {
        foreach (var resource in Model.GetComputeResources())
        {
            if (resource is not IComputeResource compute)
            {
                continue;
            }

            if (compute.HasAnnotationOfType<SkipPulumiTranslationAnnotation>())
            {
                continue;
            }

            // Honor explicit targeting: a resource pinned to a different compute environment via
            // WithComputeEnvironment must not be translated here. A null target means "any".
            var target = compute.GetComputeEnvironment();
            if (target is not null && !ReferenceEquals(target, Environment))
            {
                continue;
            }

            yield return compute;
        }
    }

    /// <summary>Registers a translated Pulumi resource for the given Aspire resource.</summary>
    /// <param name="aspireResource">The source Aspire resource.</param>
    /// <param name="pulumiResource">The Pulumi resource it was translated to.</param>
    public void RegisterTranslatedResource(IResource aspireResource, PulumiResource pulumiResource)
    {
        _translated[aspireResource] = pulumiResource;
    }

    /// <summary>Gets a previously translated Pulumi resource, or <see langword="null"/> if not found.</summary>
    /// <typeparam name="T">The expected Pulumi resource type.</typeparam>
    /// <param name="aspireResource">The source Aspire resource.</param>
    public T? GetTranslatedResource<T>(IResource aspireResource) where T : PulumiResource =>
        _translated.TryGetValue(aspireResource, out var resource) ? resource as T : null;

    /// <summary>Exports a stack output. The value is captured into <see cref="PulumiOutputReference"/> after deploy.</summary>
    /// <param name="name">The output name.</param>
    /// <param name="value">The output value.</param>
    public void AddOutput(string name, Output<string> value) => _outputs[name] = value;

    /// <summary>Exports a literal stack output.</summary>
    /// <param name="name">The output name.</param>
    /// <param name="value">The output value.</param>
    public void AddOutput(string name, string value) => _outputs[name] = Output.Create(value);

    internal IDictionary<string, object?> BuildOutputs()
    {
        var outputs = new Dictionary<string, object?>(_outputs.Count);
        foreach (var (name, value) in _outputs)
        {
            outputs[name] = value;
        }

        return outputs;
    }
}
