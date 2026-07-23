// Licensed under the MIT License.

using EmmittJ.Aspire.Hosting.Pulumi;
using Pulumi.AzureNative.Web;

namespace EmmittJ.Aspire.Hosting.Pulumi.Azure.AppService;

/// <summary>
/// Customizes the Azure App Service <see cref="WebApp"/> created for a compute resource during deploy.
/// </summary>
/// <remarks>
/// Attach via <c>PublishAsPulumiAppServiceWebsite</c>. The callback runs after the Web App is created
/// inside the Pulumi program.
/// </remarks>
public sealed class PulumiAzureAppServiceWebsiteCustomizationAnnotation : PulumiCustomizationAnnotation<WebApp>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PulumiAzureAppServiceWebsiteCustomizationAnnotation"/> class.
    /// </summary>
    /// <param name="configure">The callback that customizes the Web App.</param>
    public PulumiAzureAppServiceWebsiteCustomizationAnnotation(Action<WebApp, PulumiPublishingContext> configure)
        : base(configure)
    {
    }
}
