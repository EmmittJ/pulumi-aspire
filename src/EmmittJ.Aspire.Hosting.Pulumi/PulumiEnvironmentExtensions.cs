// Licensed under the MIT License.

#pragma warning disable ASPIRECOMPUTE002 // IComputeEnvironmentResource is experimental

using Aspire.Hosting.ApplicationModel;
using EmmittJ.Aspire.Hosting.Pulumi;

// Extension methods that operate on IResourceBuilder live in the Aspire.Hosting namespace so they are
// discoverable without an extra using, matching the official Aspire integrations. The resource types remain
// in the EmmittJ.Aspire.Hosting.Pulumi package namespace.
namespace Aspire.Hosting;

/// <summary>
/// Extension methods for adopting native Aspire compute environments with a Pulumi environment
/// (the adopt-and-traverse model) and for configuring the resulting <see cref="PulumiEnvironmentResource"/>.
/// </summary>
public static class PulumiEnvironmentExtensions
{
    /// <summary>
    /// Adopts a native Aspire compute environment with a Pulumi environment: the native environment's
    /// execution steps (provisioning, CLI logins, deploys, destroys) are suppressed, and a
    /// <see cref="PulumiEnvironmentResource"/> splices Pulumi-owned publish/deploy/destroy steps into the
    /// same pipeline slots. The native environment's prepare/publish modeling steps keep running and fully
    /// materialize the provisioning model (Bicep templates, deployment targets, manifests) that
    /// <paramref name="program"/> walks via <see cref="PulumiPublishingContext.GetDeploymentTargets"/>.
    /// </summary>
    /// <typeparam name="T">The native compute environment resource type.</typeparam>
    /// <param name="builder">The native environment resource builder (for example the result of
    /// <c>AddAzureContainerAppEnvironment</c> or <c>AddAzureAppServiceEnvironment</c>).</param>
    /// <param name="selector">
    /// The data-driven selector identifying the environment's execution steps to suppress. Use the shipped
    /// well-known selectors (<see cref="PulumiStepSuppressionSelector.AzureContainerApps"/>,
    /// <see cref="PulumiStepSuppressionSelector.AzureAppService"/>) for the built-in environments.
    /// </param>
    /// <param name="program">
    /// The Pulumi program run for publish previews, deploys, and destroys. It receives a
    /// <see cref="PulumiPublishingContext"/> to walk the materialized deployment targets and create the
    /// corresponding Pulumi resources.
    /// </param>
    /// <param name="suppressionResourceFilter">
    /// Selects the resources whose pipeline step annotations are wrapped for suppression. Defaults to all
    /// resources in the model, because native environments spread their execution steps across implicitly
    /// added resources (for example the Azure environment and container registry resources). Narrow this
    /// when multiple environments coexist and only one is adopted.
    /// </param>
    /// <param name="registryPhase">
    /// The registry-first Pulumi phase that provisions the environment's container registry (into the
    /// dedicated <c>{project}-registry</c> stack) and authenticates Docker to it before Aspire's push step
    /// runs. Required when the adopted environment pushes images to a registry it provisions itself (Azure
    /// Container Apps / App Service), because the suppressed native registry login step must be replaced —
    /// use the Azure package's <c>PulumiAzureAdoptionExtensions.CreateAzureRegistryPhase</c> for those.
    /// </param>
    /// <param name="configureEnvironment">
    /// Optional callback that configures the created <see cref="PulumiEnvironmentResource"/> (for example
    /// <see cref="WithStackName(IResourceBuilder{PulumiEnvironmentResource}, string)"/> or setting the
    /// working directory).
    /// </param>
    /// <returns>The same builder for chaining.</returns>
    /// <remarks>
    /// <para>
    /// In run mode this is a no-op: the local <c>aspire run</c> experience is untouched. In publish mode the
    /// suppression happens immediately (at builder time), so call this after the native environment — and any
    /// resources it implicitly adds — is on the builder; step annotations added later are not wrapped.
    /// </para>
    /// <para>
    /// ⚠️ For Azure Container Apps, suppressing the <c>login-to-acr-*</c> step means the Pulumi environment
    /// must supply registry credentials before Aspire's push step runs — the one native behavior that is
    /// replaced rather than merely skipped. Pass <paramref name="registryPhase"/> to resolve that seam.
    /// </para>
    /// </remarks>
    public static IResourceBuilder<T> PublishAsPulumi<T>(
        this IResourceBuilder<T> builder,
        PulumiStepSuppressionSelector selector,
        Func<PulumiPublishingContext, Task> program,
        Func<IResource, bool>? suppressionResourceFilter = null,
        PulumiRegistryPhase? registryPhase = null,
        Action<IResourceBuilder<PulumiEnvironmentResource>>? configureEnvironment = null)
        where T : IComputeEnvironmentResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(program);

        var applicationBuilder = builder.ApplicationBuilder;

        // Leave the local development experience untouched: no suppression, no Pulumi environment resource.
        if (applicationBuilder.ExecutionContext.IsRunMode)
        {
            return builder;
        }

        if (applicationBuilder.Resources.OfType<PulumiEnvironmentResource>()
                .Any(environment => ReferenceEquals(environment.AdoptedEnvironment, builder.Resource)))
        {
            throw new InvalidOperationException(
                $"The compute environment '{builder.Resource.Name}' is already adopted by a Pulumi environment. " +
                "Call 'PublishAsPulumi' at most once per environment.");
        }

        applicationBuilder.AddPulumiInfrastructureCore();

        NativePipelineStepAdoption.SuppressExecutionSteps(
            applicationBuilder,
            suppressionResourceFilter ?? (_ => true),
            selector);

        var environment = new PulumiEnvironmentResource($"{builder.Resource.Name}-pulumi", builder.Resource, program)
        {
            RegistryPhase = registryPhase,
        };
        var environmentBuilder = applicationBuilder.AddResource(environment);
        configureEnvironment?.Invoke(environmentBuilder);

        return builder;
    }

    /// <summary>
    /// Overrides the Pulumi stack name for the environment, bypassing the deploy-time Aspire environment default.
    /// </summary>
    /// <param name="builder">The environment resource builder.</param>
    /// <param name="stackName">The explicit Pulumi stack name.</param>
    /// <returns>The same builder for chaining.</returns>
    /// <remarks>
    /// By default the stack is the Aspire environment selected with <c>aspire deploy --environment &lt;name&gt;</c>,
    /// mirroring how <c>Aspire.Hosting.Kubernetes</c> derives its Helm release name. Use this escape hatch when the
    /// Pulumi stack must be decoupled from the Aspire environment name.
    /// </remarks>
    public static IResourceBuilder<PulumiEnvironmentResource> WithStackName(
        this IResourceBuilder<PulumiEnvironmentResource> builder,
        string stackName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(stackName);

        builder.Resource.StackNameOverride = stackName;
        return builder;
    }
}
