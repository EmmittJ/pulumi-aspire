// Licensed under the MIT License.

#pragma warning disable ASPIREPIPELINES001 // Pipeline APIs are experimental

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;

namespace EmmittJ.Aspire.Hosting.Pulumi;

/// <summary>
/// The deployment-target resource attached to a compute resource via <see cref="DeploymentTargetAnnotation"/>
/// when it is published through a Pulumi environment.
/// </summary>
/// <remarks>
/// <para>
/// This resource is created by the environment's <c>prepare</c> pipeline step. It is intentionally not added
/// to the application model; it exists so the compute resource can carry a deployment-target annotation, own a
/// per-resource print-summary pipeline step, and hold the <see cref="PulumiOutputReference"/> instances that
/// receive stack output values after deploy.
/// </para>
/// <para>
/// Image build and push steps are created automatically by Aspire for project and container resources, so this
/// resource does not create them. The actual cloud resource is provisioned inside the environment's Pulumi
/// program, not here.
/// </para>
/// </remarks>
public sealed class PulumiDeploymentTargetResource : Resource, IResourceWithParent<PulumiEnvironmentResource>
{
    private readonly List<PulumiOutputReference> _outputs = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="PulumiDeploymentTargetResource"/> class.
    /// </summary>
    /// <param name="name">The deployment-target resource name.</param>
    /// <param name="sourceResource">The source Aspire compute resource being deployed.</param>
    /// <param name="environment">The Pulumi environment that owns this target.</param>
    public PulumiDeploymentTargetResource(
        string name,
        IComputeResource sourceResource,
        PulumiEnvironmentResource environment)
        : base(name)
    {
        SourceResource = sourceResource;
        Parent = environment;

        // Each target owns a per-resource print-summary step. The environment expands these child steps
        // into the pipeline because this resource is not part of the application model.
        Annotations.Add(new PipelineStepAnnotation(_ =>
        {
            var printSummary = new PipelineStep
            {
                Name = PulumiPipelineSteps.PrintSummary(SourceResource.Name),
                Description = $"Prints the Pulumi deployment summary for {SourceResource.Name}.",
                Action = PrintSummaryAsync,
                Tags = [PulumiPipelineSteps.PrintSummaryTag],
                RequiredBySteps = [WellKnownPipelineSteps.Deploy],
                Resource = this,
            };

            return new List<PipelineStep> { printSummary };
        }));
    }

    /// <summary>Gets the source Aspire compute resource being deployed.</summary>
    public IComputeResource SourceResource { get; }

    /// <inheritdoc />
    public PulumiEnvironmentResource Parent { get; }

    /// <summary>Gets the output references that receive stack output values after deploy.</summary>
    public IReadOnlyList<PulumiOutputReference> Outputs => _outputs;

    /// <summary>
    /// Creates and registers a <see cref="PulumiOutputReference"/> for a named stack output of this resource.
    /// </summary>
    /// <param name="outputName">The stack output name the provider exports for this resource.</param>
    public PulumiOutputReference GetOutput(string outputName)
    {
        var existing = _outputs.FirstOrDefault(o => o.Name == outputName);
        if (existing is not null)
        {
            return existing;
        }

        var reference = new PulumiOutputReference(outputName, SourceResource);
        _outputs.Add(reference);
        return reference;
    }

    private async Task PrintSummaryAsync(PipelineStepContext context)
    {
        var outputs = Parent.LastOutputs;
        var prefix = SourceResource.Name.ToLowerInvariant().Replace("_", "-");

        // Surface any URL/FQDN output the provider exported for this resource, e.g. "{name}-url".
        var url = outputs.FirstOrDefault(kvp =>
            kvp.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            (kvp.Key.EndsWith("url", StringComparison.OrdinalIgnoreCase) ||
             kvp.Key.EndsWith("fqdn", StringComparison.OrdinalIgnoreCase))).Value;

        var message = string.IsNullOrEmpty(url)
            ? $"Deployed **{SourceResource.Name}**."
            : $"Deployed **{SourceResource.Name}** at {url}";

        await context.ReportingStep.CompleteAsync(
            message,
            CompletionState.Completed,
            context.CancellationToken).ConfigureAwait(false);
    }
}
