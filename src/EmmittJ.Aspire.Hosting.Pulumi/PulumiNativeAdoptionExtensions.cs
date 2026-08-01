// Licensed under the MIT License.

#pragma warning disable ASPIRECOMPUTE002 // IComputeEnvironmentResource is experimental

using Aspire.Hosting.ApplicationModel;
using EmmittJ.Aspire.Hosting.Pulumi;

// Extension methods that operate on IResourceBuilder live in the Aspire.Hosting namespace so they are
// discoverable without an extra using, matching the official Aspire integrations. The resource types remain
// in the EmmittJ.Aspire.Hosting.Pulumi package namespace.
namespace Aspire.Hosting;

/// <summary>
/// Extension methods for adopting native Aspire compute environments with a Pulumi backend
/// (the adopt-and-traverse mode).
/// </summary>
public static class PulumiNativeAdoptionExtensions
{
    /// <summary>
    /// Adopts a native Aspire compute environment with a Pulumi backend: the environment's execution steps
    /// (provisioning, CLI logins, deploys, destroys) are suppressed, and a <see cref="PulumiBackendResource"/>
    /// splices Pulumi-owned publish/deploy/destroy steps into the same pipeline slots. The native
    /// environment's prepare/publish modeling steps keep running and fully materialize the provisioning
    /// model (Bicep templates, deployment targets, manifests) that <paramref name="program"/> walks via
    /// <see cref="PulumiAdoptionContext.GetDeploymentTargets"/>.
    /// </summary>
    /// <typeparam name="T">The native compute environment resource type.</typeparam>
    /// <param name="builder">The native environment resource builder (for example the result of
    /// <c>AddAzureContainerAppEnvironment</c>, <c>AddAzureAppServiceEnvironment</c>,
    /// <c>AddKubernetesEnvironment</c>, or
    /// <c>AddDockerComposeEnvironment</c>).</param>
    /// <param name="selector">
    /// The data-driven selector identifying the environment's execution steps to suppress. Use the shipped
    /// well-known selectors (<see cref="PulumiStepSuppressionSelector.AzureContainerApps"/>,
    /// <see cref="PulumiStepSuppressionSelector.AzureAppService"/>,
    /// <see cref="PulumiStepSuppressionSelector.Kubernetes"/>,
    /// <see cref="PulumiStepSuppressionSelector.DockerCompose"/>) for the built-in environments.
    /// </param>
    /// <param name="program">
    /// The Pulumi program run for publish previews, deploys, and destroys. It receives a
    /// <see cref="PulumiAdoptionContext"/> to walk the materialized deployment targets and create the
    /// corresponding Pulumi resources.
    /// </param>
    /// <param name="suppressionResourceFilter">
    /// Selects the resources whose pipeline step annotations are wrapped for suppression. Defaults to all
    /// resources in the model, because native environments spread their execution steps across implicitly
    /// added resources (for example the Azure environment and container registry resources). Narrow this
    /// when multiple environments coexist and only one is adopted.
    /// </param>
    /// <returns>The same builder for chaining.</returns>
    /// <remarks>
    /// <para>
    /// In run mode this is a no-op: the local <c>aspire run</c> experience is untouched. In publish mode the
    /// suppression happens immediately (at builder time), so call this after the native environment — and any
    /// resources it implicitly adds — is on the builder; step annotations added later are not wrapped.
    /// </para>
    /// <para>
    /// ⚠️ For Azure Container Apps, suppressing the <c>login-to-acr-*</c> step means the Pulumi backend must
    /// supply registry credentials before Aspire's push step runs — the one native behavior that is replaced
    /// rather than merely skipped. Resolving that seam is up to <paramref name="program"/> (or a
    /// provider-specific frontend) until the credential-injection helper ships.
    /// </para>
    /// </remarks>
    public static IResourceBuilder<T> PublishAsPulumi<T>(
        this IResourceBuilder<T> builder,
        PulumiStepSuppressionSelector selector,
        Func<PulumiAdoptionContext, Task> program,
        Func<IResource, bool>? suppressionResourceFilter = null)
        where T : IComputeEnvironmentResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(program);

        var applicationBuilder = builder.ApplicationBuilder;

        // Leave the local development experience untouched: no suppression, no backend resource.
        if (applicationBuilder.ExecutionContext.IsRunMode)
        {
            return builder;
        }

        if (applicationBuilder.Resources.OfType<PulumiBackendResource>()
                .Any(backend => ReferenceEquals(backend.AdoptedEnvironment, builder.Resource)))
        {
            throw new InvalidOperationException(
                $"The compute environment '{builder.Resource.Name}' is already adopted by a Pulumi backend. " +
                "Call 'PublishAsPulumi' at most once per environment.");
        }

        applicationBuilder.AddPulumiInfrastructureCore();

        NativePipelineStepAdoption.SuppressExecutionSteps(
            applicationBuilder,
            suppressionResourceFilter ?? (_ => true),
            selector);

        var backend = new PulumiBackendResource($"{builder.Resource.Name}-pulumi", builder.Resource, program);
        applicationBuilder.AddResource(backend);

        return builder;
    }
}
