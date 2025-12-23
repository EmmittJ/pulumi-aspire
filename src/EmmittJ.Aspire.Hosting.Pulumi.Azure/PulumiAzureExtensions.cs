// Licensed under the Apache License, Version 2.0.

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Lifecycle;
using EmmittJ.Aspire.Hosting.Pulumi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pulumi.AzureNative.App;

namespace EmmittJ.Aspire.Hosting.Pulumi.Azure;

/// <summary>
/// Extension methods for adding Pulumi Azure environment to an Aspire application.
/// </summary>
public static class PulumiAzureExtensions
{
    /// <summary>
    /// Adds a Pulumi Azure environment for deploying resources to Azure Container Apps.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The name of the environment (used in Pulumi stack naming).</param>
    /// <param name="projectName">
    /// The Pulumi project name for grouping stacks in the Pulumi console.
    /// If not specified, defaults to <paramref name="name"/>.
    /// </param>
    /// <returns>A resource builder for further configuration.</returns>
    /// <remarks>
    /// <para>
    /// All Pulumi stacks are grouped under the <paramref name="projectName"/> in the Pulumi console.
    /// Stack names follow the pattern:
    /// </para>
    /// <list type="bullet">
    /// <item><c>{projectName}-{name}-registry</c> for the container registry stack</item>
    /// <item><c>{projectName}-{name}</c> for the main environment stack</item>
    /// </list>
    /// <para>
    /// This method registers the <see cref="PulumiAzureInfrastructure"/> event subscriber
    /// which processes compute resources during the <c>BeforeStartEvent</c> and attaches
    /// <see cref="DeploymentTargetAnnotation"/> to each one.
    /// </para>
    /// <para>
    /// This follows the Aspire-native pattern used by Azure Container Apps, Kubernetes,
    /// and Docker Compose environments.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var builder = DistributedApplication.CreateBuilder(args);
    /// 
    /// // Simple usage - project and environment both named "prod"
    /// var azure = builder.AddPulumiAzureEnvironment("prod")
    ///     .WithLocation("eastus");
    /// 
    /// // With custom project name - stacks grouped under "my-app" project
    /// var azure = builder.AddPulumiAzureEnvironment("dev", "my-app")
    ///     .WithLocation("eastus");
    /// // Creates stacks: my-app/my-app-dev-registry, my-app/my-app-dev
    /// 
    /// var cache = builder.AddRedis("cache");
    /// var api = builder.AddProject&lt;Projects.Api&gt;("api")
    ///     .WithReference(cache);
    /// 
    /// builder.Build().Run();
    /// </code>
    /// </example>
    public static IResourceBuilder<PulumiAzureEnvironmentResource> AddPulumiAzureEnvironment(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        string? projectName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var resource = projectName is not null
            ? new PulumiAzureEnvironmentResource(name, projectName)
            : new PulumiAzureEnvironmentResource(name);

        // Register the environment resource as a singleton for DI
        builder.Services.AddSingleton(resource);

        // Register the infrastructure event subscriber (Aspire-native pattern)
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IDistributedApplicationEventingSubscriber, PulumiAzureInfrastructure>());

        // Add the container registry to the app model so its pipeline steps are discovered
        // The registry is a separate resource with its own pipeline steps
        builder.AddResource(resource.ContainerRegistry);

        return builder.AddResource(resource);
    }

    /// <summary>
    /// Configures the Azure region for the environment.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="location">The Azure region (e.g., "eastus", "westus2").</param>
    /// <returns>The resource builder for chaining.</returns>
    public static IResourceBuilder<PulumiAzureEnvironmentResource> WithLocation(
        this IResourceBuilder<PulumiAzureEnvironmentResource> builder,
        string location)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(location);

        builder.Resource.Location = location;
        return builder;
    }

    /// <summary>
    /// Configures an existing resource group to use.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="resourceGroupName">The name of an existing resource group.</param>
    /// <returns>The resource builder for chaining.</returns>
    public static IResourceBuilder<PulumiAzureEnvironmentResource> WithResourceGroup(
        this IResourceBuilder<PulumiAzureEnvironmentResource> builder,
        string resourceGroupName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(resourceGroupName);

        builder.Resource.ResourceGroupName = resourceGroupName;
        return builder;
    }

    /// <summary>
    /// Configures an existing Container Apps managed environment to use.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="managedEnvironmentName">The name of an existing managed environment.</param>
    /// <returns>The resource builder for chaining.</returns>
    public static IResourceBuilder<PulumiAzureEnvironmentResource> WithManagedEnvironment(
        this IResourceBuilder<PulumiAzureEnvironmentResource> builder,
        string managedEnvironmentName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(managedEnvironmentName);

        builder.Resource.ManagedEnvironmentName = managedEnvironmentName;
        return builder;
    }

    /// <summary>
    /// Adds a customization callback that will be invoked when the Container App is created.
    /// </summary>
    /// <typeparam name="T">The compute resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="configure">The callback to configure the Container App.</param>
    /// <returns>The resource builder for chaining.</returns>
    /// <example>
    /// <code>
    /// builder.AddContainer("api", "myimage")
    ///     .PublishAsPulumiContainerApp((app, ctx) =>
    ///     {
    ///         // Customize the container app
    ///     });
    /// </code>
    /// </example>
    public static IResourceBuilder<T> PublishAsPulumiContainerApp<T>(
        this IResourceBuilder<T> builder,
        Action<ContainerApp, PulumiPublishingContext> configure)
        where T : IComputeResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        builder.WithAnnotation(new PulumiAzureContainerAppCustomizationAnnotation(configure));
        return builder;
    }
}
