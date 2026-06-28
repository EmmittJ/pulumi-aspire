// Licensed under the Apache License, Version 2.0.

using Aspire.Hosting.ApplicationModel;
using PulumiResource = Pulumi.Resource;

namespace EmmittJ.Aspire.Hosting.Pulumi;

/// <summary>
/// Marks a resource so it is skipped when the Pulumi environment translates compute resources.
/// </summary>
public sealed class SkipPulumiTranslationAnnotation : IResourceAnnotation;

/// <summary>
/// Customizes a translated Pulumi resource after it has been created inside the Pulumi program.
/// </summary>
/// <remarks>
/// Provider packages derive strongly-typed customization annotations (for example one targeting an
/// Azure Container App) from <see cref="PulumiCustomizationAnnotation{TResource}"/>.
/// </remarks>
public abstract class PulumiCustomizationAnnotation : IResourceAnnotation
{
    /// <summary>
    /// Applies the customization to a translated Pulumi resource.
    /// </summary>
    /// <param name="resource">The created Pulumi resource.</param>
    /// <param name="context">The publishing context for the current deploy.</param>
    public abstract void Configure(PulumiResource resource, PulumiPublishingContext context);
}

/// <summary>
/// Customizes a translated Pulumi resource of a specific type.
/// </summary>
/// <typeparam name="TResource">The Pulumi resource type to customize.</typeparam>
public class PulumiCustomizationAnnotation<TResource> : PulumiCustomizationAnnotation
    where TResource : PulumiResource
{
    private readonly Action<TResource, PulumiPublishingContext> _configure;

    /// <summary>
    /// Initializes a new instance of the <see cref="PulumiCustomizationAnnotation{TResource}"/> class.
    /// </summary>
    /// <param name="configure">The callback that customizes the typed Pulumi resource.</param>
    public PulumiCustomizationAnnotation(Action<TResource, PulumiPublishingContext> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _configure = configure;
    }

    /// <inheritdoc />
    public override void Configure(PulumiResource resource, PulumiPublishingContext context)
    {
        // Only invoke when the created resource matches the expected type. A mismatch means the
        // annotation was attached to a resource that translates to a different Pulumi type; skip
        // silently rather than throwing inside the Pulumi program.
        if (resource is TResource typed)
        {
            _configure(typed, context);
        }
    }
}
