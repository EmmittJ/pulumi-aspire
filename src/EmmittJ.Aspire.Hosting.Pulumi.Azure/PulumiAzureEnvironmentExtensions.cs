// Licensed under the MIT License.

#pragma warning disable ASPIRECOMPUTE001 // compute-resource APIs are experimental
#pragma warning disable ASPIRECOMPUTE002 // IComputeEnvironmentResource is experimental

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using EmmittJ.Aspire.Hosting.Pulumi;
using EmmittJ.Aspire.Hosting.Pulumi.Azure;

// Extension methods that operate on IResourceBuilder live in the Aspire.Hosting namespace so they are
// discoverable without an extra using, matching the official Aspire integrations.
namespace Aspire.Hosting;

/// <summary>
/// The one-line Pulumi adoption entry point for native Azure compute environments: everything the general
/// <c>PublishAsPulumi(selector, program, ...)</c> overload asks for is inferred from the adopted
/// environment itself.
/// </summary>
public static class PulumiAzureEnvironmentExtensions
{
    /// <summary>
    /// Hands the deployment of a native Azure compute environment (<c>AddAzureContainerAppEnvironment</c>,
    /// <c>AddAzureAppServiceEnvironment</c>) to Pulumi with a single call — the whole configuration is
    /// derived from the environment being decorated:
    /// <list type="bullet">
    /// <item>The step-suppression selector is inferred from the adopted environment's type
    /// (<see cref="PulumiStepSuppressionSelector.AzureContainerApps"/> /
    /// <see cref="PulumiStepSuppressionSelector.AzureAppService"/>) — no selector to pick.</item>
    /// <item>The Pulumi program defaults to
    /// <see cref="PulumiAzureAdoptionExtensions.TranslateAzureEnvironmentAsync"/>, translating the
    /// provisioning model Aspire already materialized into Pulumi azure-native resources.</item>
    /// <item>The registry-first phase defaults to
    /// <see cref="PulumiAzureAdoptionExtensions.CreateAzureRegistryPhase"/>: the container registry Aspire
    /// modeled for the environment is provisioned into its own <c>{project}-registry</c> Pulumi stack
    /// (registries live at a different lifecycle stage — images are pushed before the main stack deploys)
    /// and Docker is authenticated to it via <c>az acr login</c>.</item>
    /// </list>
    /// </summary>
    /// <typeparam name="T">The native Azure compute environment resource type.</typeparam>
    /// <param name="builder">The native Azure environment resource builder.</param>
    /// <param name="options">
    /// Optional translation settings shared by the main and registry stacks (location, resource group,
    /// customization hook). When omitted, the Azure location falls back to the <c>azure-native:location</c>
    /// Pulumi config value.
    /// </param>
    /// <param name="program">
    /// Optional Pulumi program that replaces the default
    /// <see cref="PulumiAzureAdoptionExtensions.TranslateAzureEnvironmentAsync"/> call — use it to
    /// post-process the translation or add extra Pulumi resources. The selector and registry phase stay
    /// inferred.
    /// </param>
    /// <param name="configureEnvironment">
    /// Optional callback that configures the created <see cref="PulumiEnvironmentResource"/> (for example
    /// <c>WithStackName</c>).
    /// </param>
    /// <returns>The same builder for chaining.</returns>
    /// <remarks>
    /// <para>
    /// In run mode this is a no-op so <c>aspire run</c> stays untouched. Use the general
    /// <c>PublishAsPulumi(selector, program, ...)</c> overload for full control (custom selectors,
    /// suppression filters, or replacing the registry phase).
    /// </para>
    /// </remarks>
    /// <exception cref="NotSupportedException">
    /// The adopted environment is not one of the recognized native Azure compute environments, so no
    /// suppression selector can be inferred.
    /// </exception>
    public static IResourceBuilder<T> PublishAsPulumi<T>(
        this IResourceBuilder<T> builder,
        AzureAdoptionOptions? options = null,
        Func<PulumiPublishingContext, Task>? program = null,
        Action<IResourceBuilder<PulumiEnvironmentResource>>? configureEnvironment = null)
        where T : IAzureComputeEnvironmentResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Leave the local development experience untouched (and never fail selector inference for it).
        if (builder.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            return builder;
        }

        var selector = ResolveSelector(builder.Resource);

        return PulumiEnvironmentExtensions.PublishAsPulumi(
            builder,
            selector,
            program ?? (context => context.TranslateAzureEnvironmentAsync(options)),
            registryPhase: PulumiAzureAdoptionExtensions.CreateAzureRegistryPhase(options),
            configureEnvironment: configureEnvironment);
    }

    /// <summary>
    /// Infers the pipeline-step suppression selector from the adopted environment's type hierarchy. The
    /// type names are pinned strings for the same reason the selectors are pinned data: the environment
    /// packages are not referenced here (consumers pull in only the one they use), and an Aspire rename
    /// must fail the catalogue tests with the exact diff instead of silently suppressing nothing.
    /// </summary>
    internal static PulumiStepSuppressionSelector ResolveSelector(IAzureComputeEnvironmentResource environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        for (var type = environment.GetType(); type is not null; type = type.BaseType)
        {
            switch (type.FullName)
            {
                case "Aspire.Hosting.Azure.AppContainers.AzureContainerAppEnvironmentResource":
                    return PulumiStepSuppressionSelector.AzureContainerApps;
                case "Aspire.Hosting.Azure.AzureAppServiceEnvironmentResource":
                    return PulumiStepSuppressionSelector.AzureAppService;
            }
        }

        throw new NotSupportedException(
            $"Cannot infer the pipeline-step suppression selector for the Azure compute environment " +
            $"'{environment.Name}' of type '{environment.GetType()}'. Only the native Azure Container Apps " +
            "and Azure App Service environments are recognized. Use the PublishAsPulumi overload that " +
            "takes an explicit PulumiStepSuppressionSelector and Pulumi program instead.");
    }
}
