// Licensed under the MIT License.

using System.Globalization;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Pulumi;

namespace EmmittJ.Aspire.Hosting.Pulumi;

/// <summary>
/// Resolves Aspire structured values (parameters, connection strings, endpoint references, reference
/// expressions, Pulumi outputs) into Pulumi <see cref="Output{T}"/> values, tracking whether the value is
/// secret. Secret-bearing values are wrapped with <see cref="Output.CreateSecret{T}(T)"/> so they are
/// encrypted in Pulumi state instead of being inlined as plaintext.
/// </summary>
/// <remarks>
/// This is the shared value-resolution engine used by both the provider-specific compute contexts
/// (<see cref="PulumiComputeResourceContext"/>) and the adopt-and-translate pipeline. Endpoint resolution
/// is platform-specific, so callers that expect endpoint references supply the
/// <see cref="EndpointResolver"/>/<see cref="EndpointExpressionResolver"/> hooks; without them an endpoint
/// value fails with an actionable error.
/// </remarks>
public sealed class PulumiValueResolver
{
    private readonly DistributedApplicationExecutionContext _executionContext;
    private readonly CancellationToken _cancellationToken;

    /// <summary>
    /// Initializes a new instance of the <see cref="PulumiValueResolver"/> class.
    /// </summary>
    /// <param name="executionContext">The execution context (publish/deploy) used to resolve callback values.</param>
    /// <param name="cancellationToken">The cancellation token for the operation.</param>
    public PulumiValueResolver(
        DistributedApplicationExecutionContext executionContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionContext);
        _executionContext = executionContext;
        _cancellationToken = cancellationToken;
    }

    /// <summary>Gets the resolver for self/cross endpoint references. Platform-specific.</summary>
    public Func<EndpointReference, Output<string>>? EndpointResolver { get; init; }

    /// <summary>Gets the resolver for single endpoint property expressions. Platform-specific.</summary>
    public Func<EndpointReferenceExpression, Output<string>>? EndpointExpressionResolver { get; init; }

    /// <summary>
    /// Resolves an Aspire structured value to a Pulumi <see cref="Output{T}"/>, tracking whether the value
    /// is secret. Mirrors the value handling performed by Aspire's Azure Container Apps translator.
    /// </summary>
    /// <param name="value">The value object from an environment variable, argument, parameter, or reference expression.</param>
    public async Task<PulumiResolvedValue> ResolveAsync(object? value)
    {
        switch (value)
        {
            case null:
                return new(Output.Create(string.Empty), IsSecret: false);

            case string s:
                return new(Output.Create(s), IsSecret: false);

            case ParameterResource parameter:
            {
                var resolved = await parameter.GetValueAsync(_cancellationToken).ConfigureAwait(false) ?? string.Empty;
                // Secret parameters are wrapped so Pulumi encrypts them in state rather than storing plaintext.
                return parameter.Secret
                    ? new(Output.CreateSecret(resolved), IsSecret: true)
                    : new(Output.Create(resolved), IsSecret: false);
            }

            case EndpointReference endpoint:
                return new(ResolveEndpoint(endpoint), IsSecret: false);

            case EndpointReferenceExpression endpointExpression:
                return new(ResolveEndpointExpression(endpointExpression), IsSecret: false);

            case ConnectionStringReference connectionString:
            {
                // Connection strings frequently embed credentials; treat them as secret.
                var resolved = await ((IValueProvider)connectionString).GetValueAsync(_cancellationToken).ConfigureAwait(false) ?? string.Empty;
                return new(Output.CreateSecret(resolved), IsSecret: true);
            }

            case IResourceWithConnectionString resourceWithConnectionString:
            {
                var resolved = await resourceWithConnectionString.GetValueAsync(_cancellationToken).ConfigureAwait(false) ?? string.Empty;
                return new(Output.CreateSecret(resolved), IsSecret: true);
            }

            case PulumiOutputReference outputReference:
            {
                // The reference resolves to its deferred string value, which the environment populates from the
                // deployed stack outputs. Output references are not secret-bearing on their own.
                var resolved = await outputReference.GetValueAsync(_cancellationToken).ConfigureAwait(false) ?? string.Empty;
                return new(Output.Create(resolved), IsSecret: false);
            }

            case ReferenceExpression referenceExpression:
                return await ResolveReferenceExpressionAsync(referenceExpression).ConfigureAwait(false);

            case IValueProvider valueProvider:
            {
                var resolved = await valueProvider.GetValueAsync(
                    new ValueProviderContext { ExecutionContext = _executionContext },
                    _cancellationToken).ConfigureAwait(false) ?? string.Empty;
                return new(Output.Create(resolved), IsSecret: false);
            }

            case IManifestExpressionProvider manifestExpression:
                // No resolver available for a bare manifest expression here; surface its expression text.
                return new(Output.Create(manifestExpression.ValueExpression), IsSecret: false);

            default:
                return new(Output.Create(value.ToString() ?? string.Empty), IsSecret: false);
        }
    }

    private Output<string> ResolveEndpoint(EndpointReference endpoint)
    {
        if (EndpointResolver is null)
        {
            throw new NotSupportedException(
                $"An endpoint reference to '{endpoint.Resource.Name}/{endpoint.EndpointName}' was encountered " +
                "but this resolver has no endpoint resolution hook. Supply an EndpointResolver when constructing " +
                "the PulumiValueResolver, or resolve the endpoint on the Aspire model before translation.");
        }

        return EndpointResolver(endpoint);
    }

    private Output<string> ResolveEndpointExpression(EndpointReferenceExpression expression)
    {
        if (EndpointExpressionResolver is null)
        {
            throw new NotSupportedException(
                $"An endpoint property expression for '{expression.Endpoint.Resource.Name}/{expression.Endpoint.EndpointName}' " +
                "was encountered but this resolver has no endpoint resolution hook. Supply an EndpointExpressionResolver " +
                "when constructing the PulumiValueResolver, or resolve the endpoint on the Aspire model before translation.");
        }

        return EndpointExpressionResolver(expression);
    }

    private async Task<PulumiResolvedValue> ResolveReferenceExpressionAsync(ReferenceExpression expression)
    {
        // Simple single-provider passthrough keeps the underlying value's secret-ness and Output identity.
        if (expression.Format == "{0}" && expression.ValueProviders.Count == 1)
        {
            return await ResolveAsync(expression.ValueProviders[0]).ConfigureAwait(false);
        }

        var parts = new Output<string>[expression.ValueProviders.Count];
        var anySecret = false;

        for (var i = 0; i < expression.ValueProviders.Count; i++)
        {
            var resolved = await ResolveAsync(expression.ValueProviders[i]).ConfigureAwait(false);
            parts[i] = resolved.Value;
            anySecret |= resolved.IsSecret;
        }

        var combined = Output.All(parts).Apply(values =>
            string.Format(CultureInfo.InvariantCulture, expression.Format, [.. values.Cast<object>()]));

        // If any constituent value was secret, the whole composite is secret.
        return anySecret
            ? new(Output.CreateSecret(combined), IsSecret: true)
            : new(combined, IsSecret: false);
    }
}

/// <summary>A resolved Pulumi value plus whether it should be treated as a secret.</summary>
/// <param name="Value">The resolved Pulumi output.</param>
/// <param name="IsSecret">Whether the value contains secret material.</param>
public readonly record struct PulumiResolvedValue(Output<string> Value, bool IsSecret);
