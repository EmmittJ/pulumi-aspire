// Licensed under the Apache License, Version 2.0.

using Aspire.Hosting.ApplicationModel;
using Pulumi;
using PulumiResource = Pulumi.Resource;

namespace EmmittJ.Aspire.Hosting.Pulumi;

/// <summary>
/// Annotation for storing Pulumi-specific resource options.
/// </summary>
/// <param name="Options">Custom resource options to apply during resource creation.</param>
public sealed record PulumiOptionsAnnotation(CustomResourceOptions? Options) : IResourceAnnotation;

/// <summary>
/// Annotation indicating a resource should be skipped during Pulumi deployment.
/// </summary>
public sealed class SkipPulumiTranslationAnnotation : IResourceAnnotation;

/// <summary>
/// Base annotation for customizing Pulumi resources during translation.
/// </summary>
/// <remarks>
/// <para>
/// This annotation allows users to customize the created Pulumi resources
/// by providing a callback that is invoked after the resource is created.
/// </para>
/// <para>
/// Provider packages define type-safe derived annotations for specific
/// resource types (e.g., <c>PulumiAzureContainerAppCustomizationAnnotation</c>).
/// </para>
/// </remarks>
public class PulumiCustomizationAnnotation : IResourceAnnotation
{
    private readonly Action<PulumiResource, PulumiPublishingContext> _configure;

    /// <summary>
    /// Initializes a new instance of the <see cref="PulumiCustomizationAnnotation"/> class.
    /// </summary>
    /// <param name="configure">The callback to configure the Pulumi resource.</param>
    public PulumiCustomizationAnnotation(Action<PulumiResource, PulumiPublishingContext> configure)
    {
        _configure = configure ?? throw new ArgumentNullException(nameof(configure));
    }

    /// <summary>
    /// Applies the customization to the Pulumi resource.
    /// </summary>
    /// <param name="resource">The Pulumi resource to customize.</param>
    /// <param name="context">The publishing context.</param>
    public void Configure(PulumiResource resource, PulumiPublishingContext context)
    {
        _configure(resource, context);
    }
}

/// <summary>
/// Generic annotation for type-safe customization of specific Pulumi resource types.
/// </summary>
/// <typeparam name="TResource">The Pulumi resource type to customize.</typeparam>
/// <remarks>
/// <para>
/// This annotation provides type-safe access to the created Pulumi resource,
/// enabling provider-specific customizations.
/// </para>
/// <example>
/// <code>
/// builder.AddContainer("api", "myimage")
///     .WithAnnotation(new PulumiCustomizationAnnotation&lt;ContainerApp&gt;(
///         (app, ctx) => {
///             // Customize the container app
///         }));
/// </code>
/// </example>
/// </remarks>
public class PulumiCustomizationAnnotation<TResource> : PulumiCustomizationAnnotation
    where TResource : PulumiResource
{
    private readonly Action<TResource, PulumiPublishingContext> _typedConfigure;

    /// <summary>
    /// Initializes a new instance of the <see cref="PulumiCustomizationAnnotation{TResource}"/> class.
    /// </summary>
    /// <param name="configure">The callback to configure the typed Pulumi resource.</param>
    public PulumiCustomizationAnnotation(Action<TResource, PulumiPublishingContext> configure)
        : base((r, ctx) =>
        {
            if (r is TResource typed)
            {
                configure(typed, ctx);
            }
        })
    {
        _typedConfigure = configure ?? throw new ArgumentNullException(nameof(configure));
    }
}
