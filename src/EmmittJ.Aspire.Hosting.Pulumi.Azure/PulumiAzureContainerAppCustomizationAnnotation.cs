// Licensed under the Apache License, Version 2.0.

using Pulumi.AzureNative.App;

namespace EmmittJ.Aspire.Hosting.Pulumi.Azure;

/// <summary>
/// Annotation for customizing Azure Container App resources during deployment.
/// </summary>
/// <remarks>
/// <para>
/// This annotation allows users to customize the created Container App resource
/// by providing a callback that is invoked after the resource is created.
/// </para>
/// <example>
/// <code>
/// builder.AddContainer("api", "myimage")
///     .PublishAsPulumiContainerApp((containerApp, ctx) =>
///     {
///         // Customize the container app
///     });
/// </code>
/// </example>
/// </remarks>
public sealed class PulumiAzureContainerAppCustomizationAnnotation : PulumiCustomizationAnnotation<ContainerApp>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PulumiAzureContainerAppCustomizationAnnotation"/> class.
    /// </summary>
    /// <param name="configure">The callback to configure the Container App.</param>
    public PulumiAzureContainerAppCustomizationAnnotation(
        Action<ContainerApp, PulumiPublishingContext> configure)
        : base(configure)
    {
    }
}
