// Licensed under the MIT License.

using Aspire.Hosting.ApplicationModel;
using EmmittJ.Aspire.Hosting.Pulumi;
using EmmittJ.Aspire.Hosting.Pulumi.Azure.AppService;
using Pulumi.AzureNative.Web;

// Extension methods that operate on IDistributedApplicationBuilder / IResourceBuilder live in the
// Aspire.Hosting namespace so they are discoverable without an extra using, matching every official
// Aspire integration (Kubernetes, Azure Container Apps, Docker Compose). The resource, context, and
// annotation types remain in the EmmittJ.Aspire.Hosting.Pulumi.Azure.AppService package namespace.
namespace Aspire.Hosting;

/// <summary>
/// Extension methods for adding a Pulumi-managed Azure App Service environment to an Aspire application.
/// </summary>
public static class PulumiAzureAppServiceExtensions
{
    /// <summary>
    /// Adds a Pulumi Azure environment that deploys compute resources to Azure App Service.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The environment resource name, used as the Pulumi project name.</param>
    /// <param name="projectName">The Pulumi project name that groups stacks. Defaults to <paramref name="name"/>.</param>
    /// <returns>A resource builder for further configuration.</returns>
    /// <remarks>
    /// The Pulumi stack (the Aspire deployment environment such as <c>dev</c>/<c>staging</c>/<c>prod</c>) is
    /// selected at deploy time with <c>aspire deploy --environment &lt;name&gt;</c>; a single environment resource
    /// therefore deploys to any number of stacks. The environment and its container registry are only added to the
    /// model in publish mode, so they never appear as resources during <c>aspire run</c>.
    /// </remarks>
    public static IResourceBuilder<PulumiAzureAppServiceEnvironmentResource> AddPulumiAzureAppServiceEnvironment(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        string? projectName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        builder.AddPulumiInfrastructureCore();

        var resource = new PulumiAzureAppServiceEnvironmentResource(name, projectName);

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
    public static IResourceBuilder<PulumiAzureAppServiceEnvironmentResource> WithLocation(
        this IResourceBuilder<PulumiAzureAppServiceEnvironmentResource> builder,
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
    public static IResourceBuilder<PulumiAzureAppServiceEnvironmentResource> WithResourceGroup(
        this IResourceBuilder<PulumiAzureAppServiceEnvironmentResource> builder,
        string resourceGroupName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(resourceGroupName);

        builder.Resource.ResourceGroupName = resourceGroupName;
        return builder;
    }

    /// <summary>Configures the App Service plan SKU.</summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="skuName">The SKU name (for example <c>P0v3</c> or <c>P1v3</c>).</param>
    /// <param name="skuTier">The SKU tier (for example <c>Premium0V3</c>).</param>
    public static IResourceBuilder<PulumiAzureAppServiceEnvironmentResource> WithPlanSku(
        this IResourceBuilder<PulumiAzureAppServiceEnvironmentResource> builder,
        string skuName,
        string skuTier)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(skuName);
        ArgumentException.ThrowIfNullOrEmpty(skuTier);

        builder.Resource.PlanSkuName = skuName;
        builder.Resource.PlanSkuTier = skuTier;
        return builder;
    }

    /// <summary>Configures whether HTTP endpoints are upgraded to HTTPS. Enabled by default.</summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="upgrade">Whether to upgrade HTTP endpoints to HTTPS.</param>
    public static IResourceBuilder<PulumiAzureAppServiceEnvironmentResource> WithHttpsUpgrade(
        this IResourceBuilder<PulumiAzureAppServiceEnvironmentResource> builder,
        bool upgrade = true)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.HttpsUpgrade = upgrade;
        return builder;
    }

    /// <summary>Adds a callback that customizes the Azure Web App created for this resource during deploy.</summary>
    /// <typeparam name="T">The compute resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="configure">The callback that customizes the Web App.</param>
    public static IResourceBuilder<T> PublishAsPulumiAppServiceWebsite<T>(
        this IResourceBuilder<T> builder,
        Action<WebApp, PulumiPublishingContext> configure)
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

        builder.WithAnnotation(new PulumiAzureAppServiceWebsiteCustomizationAnnotation(configure));
        return builder;
    }
}
