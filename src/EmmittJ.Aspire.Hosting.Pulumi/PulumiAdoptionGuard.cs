// Licensed under the MIT License.

#pragma warning disable ASPIREPIPELINES001 // Pipeline APIs are experimental
#pragma warning disable ASPIREPIPELINES002 // Pipeline tag APIs are experimental

using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;

namespace EmmittJ.Aspire.Hosting.Pulumi;

/// <summary>
/// Configuration-time invariant guard for adopt-and-traverse: after all pipeline step factories have run,
/// verifies that no native execution step of an adopted resource escaped suppression. Suppression happens at
/// builder time (see <see cref="NativePipelineStepAdoption"/>), so steps registered <em>after</em>
/// <c>PublishAsPulumi</c> would silently execute alongside Pulumi — deploying twice or destroying real
/// infrastructure. The guard turns that silent drift into a loud, actionable pipeline failure.
/// </summary>
/// <remarks>
/// A step is flagged when it is owned by an adopted resource, is not a suppressed no-op clone, is not
/// protected by a keep rule, and either (a) matches the suppression selector (it would have been suppressed
/// had it existed at adoption time), or (b) sits inside the deploy phase (ordered after
/// <see cref="WellKnownPipelineSteps.DeployPrereq"/> and before <see cref="WellKnownPipelineSteps.Deploy"/>)
/// or the destroy phase (ordered after <see cref="WellKnownPipelineSteps.DestroyPrereq"/> or before
/// <see cref="WellKnownPipelineSteps.Destroy"/>) without being cosmetic (summary/printer steps).
/// </remarks>
internal static class PulumiAdoptionGuard
{
    /// <summary>Validates the fully materialized step graph for the given Pulumi environment.</summary>
    /// <param name="context">The pipeline configuration context (all steps have been created).</param>
    /// <param name="environment">The Pulumi environment that adopted the native environment.</param>
    /// <param name="selector">The suppression selector used at adoption time.</param>
    /// <param name="resourceFilter">The resource filter used at adoption time.</param>
    /// <param name="tracker">The tracker recording the no-op clones produced at adoption time.</param>
    /// <exception cref="InvalidOperationException">An execution step escaped suppression.</exception>
    public static void Validate(
        PipelineConfigurationContext context,
        PulumiEnvironmentResource environment,
        PulumiStepSuppressionSelector selector,
        Func<IResource, bool> resourceFilter,
        PulumiStepSuppressionTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(resourceFilter);
        ArgumentNullException.ThrowIfNull(tracker);

        var (forward, backward) = BuildOrderingGraph(context.Steps);
        var afterDeployPrereq = Reach(forward, WellKnownPipelineSteps.DeployPrereq);
        var beforeDeploy = Reach(backward, WellKnownPipelineSteps.Deploy);
        var afterDestroyPrereq = Reach(forward, WellKnownPipelineSteps.DestroyPrereq);
        var beforeDestroy = Reach(backward, WellKnownPipelineSteps.Destroy);

        List<string>? problems = null;
        foreach (var step in context.Steps)
        {
            // The pipeline assigns each step's owning resource when collecting annotation-produced steps;
            // aggregate steps (publish/deploy/destroy slots) have no owner and are never suppression targets.
            var owner = step.Resource;
            if (owner is null or PulumiEnvironmentResource || !resourceFilter(owner))
            {
                continue;
            }

            if (tracker.IsSuppressed(step) || selector.IsKept(step))
            {
                continue;
            }

            if (selector.Matches(step, owner))
            {
                (problems ??= []).Add(
                    $"'{step.Name}' (resource '{owner.Name}') matches the suppression selector but was registered after adoption, so it would execute anyway.");
            }
            else if (!IsCosmetic(step)
                && ((afterDeployPrereq.Contains(step.Name) && beforeDeploy.Contains(step.Name))
                    || (afterDestroyPrereq.Contains(step.Name) && beforeDestroy.Contains(step.Name))))
            {
                (problems ??= []).Add(
                    $"'{step.Name}' (resource '{owner.Name}') runs in the deploy or destroy phase but is not covered by the suppression selector.");
            }
        }

        if (problems is not null)
        {
            var message = new StringBuilder()
                .AppendLine($"Native pipeline execution steps escaped Pulumi adoption of environment '{environment.AdoptedEnvironment.Name}' (via '{environment.Name}'). Left active, they would provision, deploy, or destroy infrastructure alongside Pulumi:")
                .AppendJoin(Environment.NewLine, problems.Select(problem => $"  - {problem}"))
                .AppendLine()
                .Append("Fixes: call PublishAsPulumi after all environment-related resources and integrations have been added; ")
                .Append("extend the selector's StepNames/StepNamePrefixes/Tags to suppress a step; ")
                .Append("or add it to KeepStepNames/KeepStepNamePrefixes/KeepTags to declare it intentionally kept.")
                .ToString();
            throw new InvalidOperationException(message);
        }
    }

    /// <summary>
    /// Builds the name-based ordering graph from both edge declarations: <c>A → B</c> means A runs before B,
    /// derived from <see cref="PipelineStep.DependsOnSteps"/> (predecessors) and
    /// <see cref="PipelineStep.RequiredBySteps"/> (successors). Names without a step object (aggregates such
    /// as <c>deploy</c>) participate as plain graph nodes.
    /// </summary>
    private static (Dictionary<string, List<string>> Forward, Dictionary<string, List<string>> Backward) BuildOrderingGraph(
        IReadOnlyList<PipelineStep> steps)
    {
        var forward = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var backward = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var step in steps)
        {
            foreach (var predecessor in step.DependsOnSteps)
            {
                AddEdge(forward, predecessor, step.Name);
                AddEdge(backward, step.Name, predecessor);
            }

            foreach (var successor in step.RequiredBySteps)
            {
                AddEdge(forward, step.Name, successor);
                AddEdge(backward, successor, step.Name);
            }
        }

        return (forward, backward);

        static void AddEdge(Dictionary<string, List<string>> edges, string from, string to)
        {
            if (!edges.TryGetValue(from, out var targets))
            {
                edges[from] = targets = [];
            }

            targets.Add(to);
        }
    }

    /// <summary>Returns every step name reachable from <paramref name="start"/> (excluding the start itself).</summary>
    private static HashSet<string> Reach(Dictionary<string, List<string>> edges, string start)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(start);

        while (queue.TryDequeue(out var current))
        {
            if (!edges.TryGetValue(current, out var targets))
            {
                continue;
            }

            foreach (var target in targets)
            {
                if (visited.Add(target))
                {
                    queue.Enqueue(target);
                }
            }
        }

        return visited;
    }

    /// <summary>
    /// Cosmetic steps (summary/dashboard printers) legitimately sit inside the deploy phase without executing
    /// anything; they keep running under adoption. The markers are guard-only strings: if Aspire drifts them,
    /// the guard fails loudly (never silently executes infrastructure work).
    /// </summary>
    private static bool IsCosmetic(PipelineStep step) =>
        step.Tags.Contains("print-summary")
        || step.Name.StartsWith("print-", StringComparison.Ordinal);
}
