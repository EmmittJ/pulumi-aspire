// Licensed under the MIT License.

#pragma warning disable ASPIREPIPELINES001 // Pipeline APIs are experimental

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Logging;

namespace EmmittJ.Aspire.Hosting.Pulumi;

/// <summary>
/// Suppresses the execution pipeline steps of a native Aspire compute environment so a Pulumi environment can
/// splice its own steps into the same slots while the native prepare/publish phases keep materializing the
/// provisioning model.
/// </summary>
/// <remarks>
/// <para>
/// Two Aspire API facts drive this design (verified against 13.4.6, see
/// <c>docs/spikes/native-environment-step-adoption.md</c>): <see cref="PipelineStep.Action"/> and
/// <c>PipelineConfigurationContext.Steps</c> are init-only, so a configuration callback can rewire edges but
/// cannot remove or replace steps. Suppression therefore happens at builder time, before build: each adopted
/// resource's <see cref="PipelineStepAnnotation"/>s are re-wrapped so steps matching the suppression selector
/// are substituted with same-name no-op clones. Because a clone keeps the original name, tags, and
/// DependsOn/RequiredBy edges, the step graph stays identical — every other step schedules exactly as before
/// and graph validation is untouched.
/// </para>
/// <para>
/// Call after the native environment (and any resources it implicitly adds, such as the Azure environment and
/// container registry resources) has been added to the builder; annotations added later are not wrapped.
/// </para>
/// </remarks>
public static class NativePipelineStepAdoption
{
    /// <summary>
    /// Wraps every <see cref="PipelineStepAnnotation"/> on resources matching <paramref name="resourceFilter"/>
    /// so steps matching <paramref name="selector"/> are replaced by same-name no-op clones.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="resourceFilter">Selects the adopted native resources whose step annotations are wrapped.</param>
    /// <param name="selector">The data-driven selector identifying the execution steps to suppress.</param>
    public static void SuppressExecutionSteps(
        IDistributedApplicationBuilder builder,
        Func<IResource, bool> resourceFilter,
        PulumiStepSuppressionSelector selector)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(resourceFilter);
        ArgumentNullException.ThrowIfNull(selector);

        foreach (var resource in builder.Resources.Where(resourceFilter))
        {
            var originals = resource.Annotations.OfType<PipelineStepAnnotation>().ToList();
            foreach (var original in originals)
            {
                resource.Annotations.Remove(original);
                resource.Annotations.Add(new PipelineStepAnnotation(async factoryContext =>
                {
                    var steps = await original.CreateStepsAsync(factoryContext).ConfigureAwait(false);
                    return steps.Select(step => selector.Matches(step) ? CloneAsNoOp(step) : step).ToList();
                }));
            }
        }
    }

    /// <summary>
    /// Clones a pipeline step, preserving its identity and graph edges (name, tags, DependsOn/RequiredBy)
    /// but substituting a no-op action that only logs the suppression.
    /// </summary>
    /// <param name="step">The native execution step to neutralize.</param>
    /// <returns>A same-name clone whose action does nothing.</returns>
    public static PipelineStep CloneAsNoOp(PipelineStep step)
    {
        ArgumentNullException.ThrowIfNull(step);

        return new()
        {
            Name = step.Name,
            Description = step.Description,
            Action = context =>
            {
                context.Logger.LogDebug(
                    "Skipped native pipeline step '{StepName}': its execution is owned by the Pulumi environment.",
                    step.Name);
                return Task.CompletedTask;
            },
            DependsOnSteps = step.DependsOnSteps,
            RequiredBySteps = step.RequiredBySteps,
            Tags = step.Tags,
            Resource = step.Resource,
        };
    }
}
