// Licensed under the MIT License.

using EmmittJ.Aspire.Hosting.Pulumi;
using Pulumi.AzureNative.App;

namespace EmmittJ.Aspire.Hosting.Pulumi.Azure;

/// <summary>
/// Customizes the Azure <see cref="ContainerApp"/> created for a compute resource during deploy.
/// </summary>
/// <remarks>
/// Attach via <c>PublishAsPulumiContainerApp</c>. The callback runs after the Container App is created
/// inside the Pulumi program.
/// </remarks>
public sealed class PulumiAzureContainerAppCustomizationAnnotation : PulumiCustomizationAnnotation<ContainerApp>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PulumiAzureContainerAppCustomizationAnnotation"/> class.
    /// </summary>
    /// <param name="configure">The callback that customizes the Container App.</param>
    public PulumiAzureContainerAppCustomizationAnnotation(Action<ContainerApp, PulumiPublishingContext> configure)
        : base(configure)
    {
    }
}
