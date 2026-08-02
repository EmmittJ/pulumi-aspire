// Licensed under the MIT License.

#pragma warning disable ASPIREPIPELINES001 // Pipeline APIs are experimental
#pragma warning disable ASPIREPIPELINES002 // Pipeline tag APIs are experimental
#pragma warning disable ASPIRECOMPUTE003  // IContainerRegistry is experimental

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;

namespace EmmittJ.Aspire.Hosting.Pulumi;

/// <summary>
/// Identifies which native pipeline steps are <em>execution</em> steps (provisioning, CLI logins, deploys,
/// destroys) to be suppressed when a Pulumi environment adopts a native Aspire compute environment. Modeling
/// steps (prepare/publish) never match and keep running so the native environment fully materializes the
/// provisioning model the Pulumi environment walks.
/// </summary>
/// <remarks>
/// <para>
/// 🔍 By default the selector classifies steps <em>structurally</em>, using only public Aspire pipeline
/// contracts (<see cref="WellKnownPipelineSteps"/>, <see cref="WellKnownPipelineTags"/>,
/// <see cref="IContainerRegistry"/>) rather than provider-private step names. A step is an execution step
/// when any of the following hold:
/// </para>
/// <list type="bullet">
/// <item><description>it is tagged <see cref="WellKnownPipelineTags.ProvisionInfrastructure"/> (infrastructure provisioning);</description></item>
/// <item><description>it depends on <see cref="WellKnownPipelineSteps.DeployPrereq"/> (deploy-slot execution work such as CLI login validation and provisioning-context creation);</description></item>
/// <item><description>it depends on <see cref="WellKnownPipelineSteps.DestroyPrereq"/> or is required by <see cref="WellKnownPipelineSteps.Destroy"/> (teardown);</description></item>
/// <item><description>it is owned by an <see cref="IContainerRegistry"/> resource and required by <see cref="WellKnownPipelineSteps.PushPrereq"/> (registry login — the one native behavior the Pulumi environment replaces rather than merely skips).</description></item>
/// </list>
/// <para>
/// Deploy-slot classification is deliberately edge-directional: steps that merely hang <em>off</em>
/// <see cref="WellKnownPipelineSteps.Deploy"/> (for example dashboard-URL printers required by deploy) are
/// modeling/cosmetic and keep running. Destroy-slot classification is broader (either edge) because an
/// escaped teardown step is destructive.
/// </para>
/// <para>
/// The data lists (<see cref="StepNames"/>, <see cref="StepNamePrefixes"/>, <see cref="Tags"/>) widen the
/// match, and the keep lists (<see cref="KeepStepNames"/>, <see cref="KeepStepNamePrefixes"/>,
/// <see cref="KeepTags"/>) always win and narrow it, so provider quirks remain expressible without forking
/// the classifier. Catalogue tests pin that structural classification reproduces the exact per-provider
/// suppression sets on real pipelines; see <c>docs/spikes/native-environment-step-adoption.md</c>.
/// </para>
/// </remarks>
public sealed record PulumiStepSuppressionSelector
{
    /// <summary>
    /// Gets a value indicating whether steps are classified structurally via public pipeline contracts
    /// (well-known step names, tags, and graph position). Defaults to <see langword="true"/>; set to
    /// <see langword="false"/> for a purely data-driven selector.
    /// </summary>
    public bool UseStructuralClassification { get; init; } = true;

    /// <summary>Gets the exact step names to additionally suppress.</summary>
    public IReadOnlyList<string> StepNames { get; init; } = [];

    /// <summary>Gets the step-name prefixes to additionally suppress (ordinal comparison).</summary>
    public IReadOnlyList<string> StepNamePrefixes { get; init; } = [];

    /// <summary>Gets the step tags to additionally suppress.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Gets the exact step names that are never suppressed, overriding all other rules.</summary>
    public IReadOnlyList<string> KeepStepNames { get; init; } = [];

    /// <summary>Gets the step-name prefixes that are never suppressed, overriding all other rules (ordinal comparison).</summary>
    public IReadOnlyList<string> KeepStepNamePrefixes { get; init; } = [];

    /// <summary>Gets the step tags that are never suppressed, overriding all other rules.</summary>
    public IReadOnlyList<string> KeepTags { get; init; } = [];

    /// <summary>
    /// The purely structural selector: classifies execution steps by public pipeline contracts only, with no
    /// provider-specific step names. This is the default for compute environments without a hand-tuned
    /// selector and is proven equivalent to the pinned Azure selectors by catalogue tests.
    /// </summary>
    public static PulumiStepSuppressionSelector Structural { get; } = new();

    /// <summary>
    /// The execution steps of <c>AddAzureContainerAppEnvironment</c> (and its implicit
    /// <c>AzureEnvironmentResource</c> and container registry): Azure login validation, provisioning-context
    /// creation, Bicep provisioning, ACR login, and destroy. Structural classification already covers all of
    /// these; the pinned names/tags are kept as belt-and-braces so either signal alone suffices.
    /// </summary>
    /// <remarks>
    /// ⚠️ Suppressing <c>acr-login</c> means the Pulumi environment must supply registry credentials before
    /// Aspire's push step runs (the one native behavior that is replaced rather than merely skipped).
    /// </remarks>
    public static PulumiStepSuppressionSelector AzureContainerApps { get; } = new()
    {
        StepNames = ["validate-azure-login", "create-provisioning-context"],
        StepNamePrefixes = ["destroy-azure-"],
        Tags = ["provision-infra", "acr-login"],
    };

    /// <summary>
    /// The execution steps of <c>AddAzureAppServiceEnvironment</c> (and its implicit
    /// <c>AzureEnvironmentResource</c> and container registry): Azure login validation, provisioning-context
    /// creation, Bicep provisioning, ACR login, and destroy. The step surface is currently identical to
    /// <see cref="AzureContainerApps"/> because App Service reuses the same Azure environment and registry
    /// plumbing, but the selector is pinned independently so an Aspire version bump that diverges the two
    /// environments fails the App Service catalogue tests with the exact diff.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ Suppressing <c>acr-login</c> means the Pulumi environment must supply registry credentials before
    /// Aspire's push step runs (the one native behavior that is replaced rather than merely skipped).
    /// </para>
    /// <para>
    /// ⚠️ App Service only materializes <c>DeploymentTargetAnnotation</c>s for project resources and
    /// containers with a Dockerfile build (<c>AddDockerfile</c>/<c>WithDockerfile</c>); plain image
    /// containers are silently skipped by the native prepare step and never appear in
    /// <see cref="PulumiPublishingContext.GetDeploymentTargets"/>.
    /// </para>
    /// </remarks>
    public static PulumiStepSuppressionSelector AzureAppService { get; } = new()
    {
        StepNames = ["validate-azure-login", "create-provisioning-context"],
        StepNamePrefixes = ["destroy-azure-"],
        Tags = ["provision-infra", "acr-login"],
    };

    /// <summary>Determines whether the given pipeline step matches this selector.</summary>
    /// <param name="step">The pipeline step to test.</param>
    /// <returns><see langword="true"/> when the step is an execution step to suppress.</returns>
    /// <remarks>
    /// Uses <see cref="PipelineStep.Resource"/> as the owning resource for structural rules that depend on
    /// ownership. Prefer <see cref="Matches(PipelineStep, IResource?)"/> when the owner is known (native
    /// factories do not reliably set <see cref="PipelineStep.Resource"/>).
    /// </remarks>
    public bool Matches(PipelineStep step)
    {
        ArgumentNullException.ThrowIfNull(step);

        return Matches(step, step.Resource);
    }

    /// <summary>Determines whether the given pipeline step matches this selector.</summary>
    /// <param name="step">The pipeline step to test.</param>
    /// <param name="owner">The resource whose <c>PipelineStepAnnotation</c> produced the step, if known.</param>
    /// <returns><see langword="true"/> when the step is an execution step to suppress.</returns>
    public bool Matches(PipelineStep step, IResource? owner)
    {
        ArgumentNullException.ThrowIfNull(step);

        if (IsKept(step))
        {
            return false;
        }

        return MatchesData(step) || (UseStructuralClassification && MatchesStructurally(step, owner));
    }

    /// <summary>Determines whether the step is protected by a keep rule (never suppressed).</summary>
    internal bool IsKept(PipelineStep step) =>
        KeepStepNames.Contains(step.Name, StringComparer.Ordinal)
        || KeepStepNamePrefixes.Any(prefix => step.Name.StartsWith(prefix, StringComparison.Ordinal))
        || KeepTags.Any(step.Tags.Contains);

    /// <summary>Determines whether the step matches the data lists (names, prefixes, tags).</summary>
    internal bool MatchesData(PipelineStep step) =>
        StepNames.Contains(step.Name, StringComparer.Ordinal)
        || StepNamePrefixes.Any(prefix => step.Name.StartsWith(prefix, StringComparison.Ordinal))
        || Tags.Any(step.Tags.Contains);

    /// <summary>
    /// Determines whether the step is an execution step by structure alone: provisioning tag, deploy-prereq
    /// dependency, destroy edges, or a registry-owned push-prereq (login) step.
    /// </summary>
    internal static bool MatchesStructurally(PipelineStep step, IResource? owner) =>
        step.Tags.Contains(WellKnownPipelineTags.ProvisionInfrastructure)
        || step.DependsOnSteps.Contains(WellKnownPipelineSteps.DeployPrereq)
        || step.DependsOnSteps.Contains(WellKnownPipelineSteps.DestroyPrereq)
        || step.RequiredBySteps.Contains(WellKnownPipelineSteps.Destroy)
        || (owner is IContainerRegistry && step.RequiredBySteps.Contains(WellKnownPipelineSteps.PushPrereq));
}
