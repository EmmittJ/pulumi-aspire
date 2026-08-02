// Licensed under the MIT License.

#pragma warning disable ASPIREPIPELINES001 // Pipeline APIs are experimental

using System.Runtime.CompilerServices;
using Aspire.Hosting.Pipelines;

namespace EmmittJ.Aspire.Hosting.Pulumi;

/// <summary>
/// Records the no-op clones produced by <see cref="NativePipelineStepAdoption"/> so the configuration-time
/// adoption guard (<see cref="PulumiAdoptionGuard"/>) can tell suppressed steps apart from execution steps
/// that escaped suppression (for example, steps registered after <c>PublishAsPulumi</c> ran).
/// </summary>
internal sealed class PulumiStepSuppressionTracker
{
    private readonly ConditionalWeakTable<PipelineStep, object> _suppressed = [];

    /// <summary>Records that <paramref name="step"/> is a suppressed no-op clone.</summary>
    public void RecordSuppressed(PipelineStep step) => _suppressed.AddOrUpdate(step, this);

    /// <summary>Determines whether <paramref name="step"/> is a suppressed no-op clone.</summary>
    public bool IsSuppressed(PipelineStep step) => _suppressed.TryGetValue(step, out _);
}
