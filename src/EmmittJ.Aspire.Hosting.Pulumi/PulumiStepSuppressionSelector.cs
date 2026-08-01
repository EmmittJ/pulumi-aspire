// Licensed under the MIT License.

#pragma warning disable ASPIREPIPELINES001 // Pipeline APIs are experimental

using Aspire.Hosting.Pipelines;

namespace EmmittJ.Aspire.Hosting.Pulumi;

/// <summary>
/// A data-driven selector that identifies which native pipeline steps are <em>execution</em> steps
/// (provisioning, CLI logins, deploys, destroys) to be suppressed when a Pulumi backend adopts a native
/// Aspire compute environment. Modeling steps (prepare/publish) never match and keep running so the native
/// environment fully materializes the provisioning model the Pulumi backend walks.
/// </summary>
/// <remarks>
/// <para>
/// The selector is intentionally pure data (exact names, name prefixes, and tags) so per-provider selectors
/// can be pinned by catalogue tests: the step names/tags of native environments are undocumented strings, and
/// an Aspire version bump that changes them must fail tests with the exact diff instead of silently deploying
/// twice. See <c>docs/spikes/native-environment-step-adoption.md</c>.
/// </para>
/// </remarks>
public sealed record PulumiStepSuppressionSelector
{
    /// <summary>Gets the exact step names to suppress.</summary>
    public IReadOnlyList<string> StepNames { get; init; } = [];

    /// <summary>Gets the step-name prefixes to suppress (ordinal comparison).</summary>
    public IReadOnlyList<string> StepNamePrefixes { get; init; } = [];

    /// <summary>Gets the step tags to suppress.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>
    /// The execution steps of <c>AddAzureContainerAppEnvironment</c> (and its implicit
    /// <c>AzureEnvironmentResource</c> and container registry): Azure login validation, provisioning-context
    /// creation, Bicep provisioning, ACR login, and destroy.
    /// </summary>
    /// <remarks>
    /// ⚠️ Suppressing <c>acr-login</c> means the Pulumi backend must supply registry credentials before
    /// Aspire's push step runs (the one native behavior that is replaced rather than merely skipped).
    /// </remarks>
    public static PulumiStepSuppressionSelector AzureContainerApps { get; } = new()
    {
        StepNames = ["validate-azure-login", "create-provisioning-context"],
        StepNamePrefixes = ["destroy-azure-"],
        Tags = ["provision-infra", "acr-login"],
    };

    /// <summary>
    /// The execution steps of <c>AddKubernetesEnvironment</c>: everything Helm (prereq checks, deploy,
    /// uninstall) and destroy. The prepare/publish modeling steps keep running and write manifests normally.
    /// </summary>
    public static PulumiStepSuppressionSelector Kubernetes { get; } = new()
    {
        StepNamePrefixes = ["check-helm-prereqs-", "destroy-helm-"],
        Tags = ["helm-deploy", "helm-uninstall"],
    };

    /// <summary>
    /// The execution steps of <c>AddDockerComposeEnvironment</c>: <c>docker compose up</c>/<c>down</c> and
    /// destroy. The prepare/publish modeling steps keep running and write the compose artifacts normally.
    /// </summary>
    public static PulumiStepSuppressionSelector DockerCompose { get; } = new()
    {
        StepNamePrefixes = ["destroy-compose-"],
        Tags = ["docker-compose-up", "docker-compose-down"],
    };

    /// <summary>Determines whether the given pipeline step matches this selector.</summary>
    /// <param name="step">The pipeline step to test.</param>
    /// <returns><see langword="true"/> when the step is an execution step to suppress.</returns>
    public bool Matches(PipelineStep step)
    {
        ArgumentNullException.ThrowIfNull(step);

        return StepNames.Contains(step.Name, StringComparer.Ordinal)
            || StepNamePrefixes.Any(prefix => step.Name.StartsWith(prefix, StringComparison.Ordinal))
            || Tags.Any(step.Tags.Contains);
    }
}
