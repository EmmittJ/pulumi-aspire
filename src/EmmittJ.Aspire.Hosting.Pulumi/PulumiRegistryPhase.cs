// Licensed under the MIT License.

#pragma warning disable ASPIREPIPELINES001 // Pipeline APIs are experimental
#pragma warning disable ASPIRECOMPUTE003  // IContainerRegistry is experimental

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;

namespace EmmittJ.Aspire.Hosting.Pulumi;

/// <summary>
/// The registry-first Pulumi phase for an adopted native environment: a Pulumi program that provisions the
/// environment's container registry into its own stack <em>before</em> Aspire's image push step runs, plus
/// an optional login callback that authenticates Docker to the provisioned registry.
/// </summary>
/// <remarks>
/// <para>
/// This resolves the one seam where adopting a native environment must <em>replace</em> behavior rather
/// than merely skip it: suppressing the native registry login step (for example Azure Container Apps'
/// <c>login-to-acr-*</c>, required by <c>push-prereq</c>) leaves Aspire's push step without a provisioned
/// registry or credentials. When a phase is set on <see cref="PulumiBackendResource.RegistryPhase"/>, the
/// backend splices a <c>pulumi-deploy-registry-{name}</c> step (required by <c>push-prereq</c>) that runs
/// <see cref="Program"/> as a Pulumi <c>up</c> against the dedicated <c>{project}-registry</c> stack and
/// then invokes <see cref="LoginCallback"/> for each registry the adopted environment attached to its
/// deployment targets. A matching <c>pulumi-destroy-registry-{name}</c> step tears the registry stack down
/// after the main stack is destroyed.
/// </para>
/// <para>
/// The program is expected to export the registry's outputs as stack outputs so provider frontends can
/// back-propagate the deployed values into the application model (the Azure frontend's
/// <c>TranslateAzureRegistriesAsync</c> does this for <c>BicepOutputReference</c>s, which is what lets
/// Aspire's push step and <see cref="LoginCallback"/> resolve the registry name and endpoint).
/// </para>
/// </remarks>
public sealed class PulumiRegistryPhase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PulumiRegistryPhase"/> class.
    /// </summary>
    /// <param name="program">
    /// The Pulumi program that provisions the registry resources. It receives a
    /// <see cref="PulumiAdoptionContext"/> (with <see cref="PulumiAdoptionContext.Operation"/> set to
    /// <see cref="PulumiOperation.Up"/>) and must export the registry outputs it wants back-propagated.
    /// </param>
    public PulumiRegistryPhase(Func<PulumiAdoptionContext, Task> program)
    {
        ArgumentNullException.ThrowIfNull(program);
        Program = program;
    }

    /// <summary>Gets the Pulumi program that provisions the registry resources.</summary>
    public Func<PulumiAdoptionContext, Task> Program { get; }

    /// <summary>
    /// Gets or sets the callback that authenticates Docker to a provisioned registry after
    /// <see cref="Program"/> deploys. Invoked once per distinct registry the adopted environment attached
    /// to its deployment targets. Use the <see cref="PulumiContainerRegistryHelpers"/> factories for the
    /// common CLI-based logins.
    /// </summary>
    public Func<PipelineStepContext, IContainerRegistry, Task>? LoginCallback { get; set; }
}
