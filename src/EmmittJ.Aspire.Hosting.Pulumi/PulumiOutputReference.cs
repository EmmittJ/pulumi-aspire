// Licensed under the MIT License.

using Aspire.Hosting.ApplicationModel;

namespace EmmittJ.Aspire.Hosting.Pulumi;

/// <summary>
/// A reference to a named output produced by a Pulumi stack for a given resource.
/// </summary>
/// <remarks>
/// <para>
/// This is the Pulumi analogue of Aspire's <c>BicepOutputReference</c>. It is created during model
/// construction and can be passed into environment variables, args, and other structured values via
/// <c>WithReference</c>/<c>WithEnvironment</c>. The concrete value is not known until the owning
/// stack has been deployed.
/// </para>
/// <para>
/// During <c>aspire publish</c> the reference projects to its <see cref="ValueExpression"/> placeholder.
/// During <c>aspire deploy</c> the environment captures stack outputs and calls
/// <see cref="SetValue(string?)"/>, which completes <see cref="GetValueAsync(CancellationToken)"/>.
/// </para>
/// </remarks>
public sealed class PulumiOutputReference : IManifestExpressionProvider, IValueProvider, IValueWithReferences
{
    // Gated like BicepOutputReference: callers awaiting GetValueAsync block until the deploy step
    // resolves the output (or the environment faults the reference).
    private readonly TaskCompletionSource<string?> _valueSource =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Initializes a new instance of the <see cref="PulumiOutputReference"/> class.
    /// </summary>
    /// <param name="name">The stack output name.</param>
    /// <param name="resource">The Aspire resource that owns the output.</param>
    public PulumiOutputReference(string name, IResource resource)
    {
        Name = name;
        Resource = resource;
    }

    /// <summary>Gets the stack output name.</summary>
    public string Name { get; }

    /// <summary>Gets the Aspire resource that owns this output.</summary>
    public IResource Resource { get; }

    /// <summary>Gets the resolved output value once the owning stack has been deployed.</summary>
    public string? Value { get; private set; }

    /// <inheritdoc />
    public string ValueExpression => $"{{{Resource.Name}.outputs.{Name}}}";

    /// <inheritdoc />
    IEnumerable<object> IValueWithReferences.References => [Resource];

    /// <inheritdoc />
    public async ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default)
    {
        // Fast path: value already resolved (e.g. re-reads after deploy).
        if (Value is not null)
        {
            return Value;
        }

        return await _valueSource.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets the resolved output value, completing any awaiters of <see cref="GetValueAsync(CancellationToken)"/>.
    /// </summary>
    /// <param name="value">The resolved value from the deployed stack outputs.</param>
    public void SetValue(string? value)
    {
        Value = value;
        _valueSource.TrySetResult(value);
    }

    /// <summary>
    /// Faults the reference so that awaiters of <see cref="GetValueAsync(CancellationToken)"/> observe the failure
    /// instead of hanging when the owning stack fails to produce the output.
    /// </summary>
    /// <param name="exception">The failure to propagate.</param>
    public void SetException(Exception exception) => _valueSource.TrySetException(exception);
}
