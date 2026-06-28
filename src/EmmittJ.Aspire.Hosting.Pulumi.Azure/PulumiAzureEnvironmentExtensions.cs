// Licensed under the Apache License, Version 2.0.

using Aspire.Hosting.ApplicationModel;
using EmmittJ.Aspire.Hosting.Pulumi;
using EmmittJ.Aspire.Hosting.Pulumi.Azure;
using Pulumi.AzureNative.App;

// Extension methods that operate on IDistributedApplicationBuilder / IResourceBuilder live in the
// Aspire.Hosting namespace so they are discoverable without an extra using, matching every official
// Aspire integration (Kubernetes, Azure Container Apps, Docker Compose). The resource, context, and
// annotation types remain in the EmmittJ.Aspire.Hosting.Pulumi.Azure package namespace.
namespace Aspire.Hosting;

/// <summary>
/// Extension methods for adding a Pulumi-managed Azure Container Apps environment to an Aspire application.
/// </summary>
public static class PulumiAzureEnvironmentExtensions
{
    /// <summary>
    /// Adds a Pulumi Azure environment that deploys compute resources to Azure Container Apps.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The environment name (used as the Pulumi stack name).</param>
    /// <param name="projectName">The Pulumi project name that groups stacks. Defaults to <paramref name="name"/>.</param>
    /// <returns>A resource builder for further configuration.</returns>
    /// <remarks>
    /// The environment and its container registry are only added to the model in publish mode, so they never
    /// appear as resources during <c>aspire run</c>.
    /// </remarks>
    public static IResourceBuilder<PulumiAzureEnvironmentResource> AddPulumiAzureEnvironment(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        string? projectName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        builder.AddPulumiInfrastructureCore();

        var resource = new PulumiAzureEnvironmentResource(name, projectName);

        if (builder.ExecutionContext.IsRunMode)
        {
            // Return a builder that is not added to the model so the environment does not surface locally.
            return builder.CreateResourceBuilder(resource);
        }

        // Publish/deploy mode: add the registry so its provision/login pipeline steps run before image push.
        builder.AddResource(resource.Registry);
        return builder.AddResource(resource);
    }

    /// <summary>Configures the Azure region for the environment.</summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="location">The Azure region (for example <c>eastus</c>).</param>
    public static IResourceBuilder<PulumiAzureEnvironmentResource> WithLocation(
        this IResourceBuilder<PulumiAzureEnvironmentResource> builder,
        string location)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(location);

        builder.Resource.Location = location;
        return builder;
    }

    /// <summary>Configures an existing resource group to deploy into instead of creating one.</summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="resourceGroupName">The existing resource group name.</param>
    public static IResourceBuilder<PulumiAzureEnvironmentResource> WithResourceGroup(
        this IResourceBuilder<PulumiAzureEnvironmentResource> builder,
        string resourceGroupName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(resourceGroupName);

        builder.Resource.ResourceGroupName = resourceGroupName;
        return builder;
    }

    /// <summary>Configures an existing Container Apps managed environment to use instead of creating one.</summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="managedEnvironmentName">The existing managed environment name.</param>
    public static IResourceBuilder<PulumiAzureEnvironmentResource> WithManagedEnvironment(
        this IResourceBuilder<PulumiAzureEnvironmentResource> builder,
        string managedEnvironmentName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(managedEnvironmentName);

        builder.Resource.ManagedEnvironmentName = managedEnvironmentName;
        return builder;
    }

    /// <summary>Adds a callback that customizes the Azure Container App created for this resource during deploy.</summary>
    /// <typeparam name="T">The compute resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="configure">The callback that customizes the Container App.</param>
    public static IResourceBuilder<T> PublishAsPulumiContainerApp<T>(
        this IResourceBuilder<T> builder,
        Action<ContainerApp, PulumiPublishingContext> configure)
        where T : IComputeResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        // PublishAs{Target} customizations only apply to publish/deploy output. Return the builder unchanged
        // in run mode so the annotation never affects local orchestration (matches AddKubernetesEnvironment's
        // PublishAsKubernetesService contract).
        if (!builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            return builder;
        }

        // Ensure the shared Pulumi services and the global validation step are registered so a customization
        // without a Pulumi environment fails with an actionable error.
        builder.ApplicationBuilder.AddPulumiInfrastructureCore();

        builder.WithAnnotation(new PulumiAzureContainerAppCustomizationAnnotation(configure));
        return builder;
    }
}
