// Licensed under the Apache License, Version 2.0.

using Aspire.Hosting.ApplicationModel;
using Pulumi;

namespace EmmittJ.Aspire.Hosting.Pulumi;

/// <summary>
/// A reference to an output from a Pulumi resource.
/// </summary>
/// <param name="name">The name of the output.</param>
/// <param name="resource">The Aspire resource that owns the Pulumi output.</param>
/// <remarks>
/// <para>
/// This is the Pulumi equivalent of Aspire's <c>BicepOutputReference</c>.
/// It provides a simple way to reference outputs from Pulumi resources
/// that may not be known until after deployment.
/// </para>
/// </remarks>
public sealed class PulumiOutputReference(string name, IResource resource) : IManifestExpressionProvider, IValueWithReferences
{
    private Output<string>? _output;

    /// <summary>
    /// Gets the name of the output.
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    /// Gets the Aspire resource that owns this output.
    /// </summary>
    public IResource Resource { get; } = resource;

    /// <summary>
    /// Gets or sets the Pulumi output value.
    /// </summary>
    /// <remarks>
    /// This is set during resource translation when the Pulumi resource is created.
    /// </remarks>
    public Output<string>? Output
    {
        get => _output;
        set => _output = value;
    }

    /// <inheritdoc />
    public string ValueExpression => $"{{{Resource.Name}.outputs.{Name}}}";

    /// <inheritdoc />
    IEnumerable<object> IValueWithReferences.References => [Resource];

    /// <summary>
    /// Gets the resolved value of the output.
    /// </summary>
    /// <remarks>
    /// This should only be called after deployment completes and outputs are available.
    /// </remarks>
    public string? Value { get; internal set; }

    /// <summary>
    /// Gets the output value, waiting for deployment if necessary.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The output value, or null if not available.</returns>
    public ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default)
    {
        // If we already have a resolved value, return it
        if (Value is not null)
        {
            return new ValueTask<string?>(Value);
        }

        // Otherwise return null - the value will be set after deployment
        return new ValueTask<string?>((string?)null);
    }
}
