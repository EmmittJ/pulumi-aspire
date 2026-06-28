// Licensed under the MIT License.

#pragma warning disable ASPIREPIPELINES003 // ContainerImageReference is experimental

using System.Globalization;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;
using Pulumi;
using PulumiResource = Pulumi.Resource;

namespace EmmittJ.Aspire.Hosting.Pulumi;

/// <summary>
/// Base class that translates a single Aspire compute resource into a provider-specific Pulumi resource.
/// </summary>
/// <remarks>
/// <para>
/// The base class collects environment variables, command-line arguments, and endpoint metadata from the
/// source resource and provides a shared value resolver that converts Aspire structured values
/// (parameters, connection strings, endpoint references, reference expressions, Pulumi outputs) into
/// Pulumi <see cref="Output{T}"/> values. Secret-bearing values are wrapped with
/// <see cref="Output.CreateSecret{T}(T)"/> so they are encrypted in Pulumi state instead of being inlined
/// as plaintext.
/// </para>
/// <para>
/// Provider packages implement <see cref="BuildComputeResourceAsync"/> to create the cloud resource and the
/// endpoint hooks (<see cref="ResolveEndpoint"/>, <see cref="ResolveEndpointExpression"/>) which depend on
/// the target platform's addressing scheme.
/// </para>
/// </remarks>
public abstract class PulumiComputeResourceContext
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PulumiComputeResourceContext"/> class.
    /// </summary>
    /// <param name="computeResource">The source Aspire compute resource.</param>
    /// <param name="publishingContext">The publishing context for the current deploy.</param>
    protected PulumiComputeResourceContext(
        IComputeResource computeResource,
        PulumiPublishingContext publishingContext)
    {
        ComputeResource = computeResource;
        PublishingContext = publishingContext;
    }

    /// <summary>Gets the source Aspire compute resource.</summary>
    public IComputeResource ComputeResource { get; }

    /// <summary>Gets the publishing context.</summary>
    public PulumiPublishingContext PublishingContext { get; }

    /// <summary>Gets the execution context.</summary>
    protected DistributedApplicationExecutionContext ExecutionContext => PublishingContext.ExecutionContext;

    /// <summary>Gets the cancellation token.</summary>
    protected CancellationToken CancellationToken => PublishingContext.CancellationToken;

    /// <summary>Gets the logger.</summary>
    protected ILogger Logger => PublishingContext.Logger;

    /// <summary>Gets the raw environment variable values collected from the resource, keyed by variable name.</summary>
    public Dictionary<string, object> EnvironmentVariables { get; } = [];

    /// <summary>Gets the raw command-line argument values collected from the resource.</summary>
    public List<object> Args { get; } = [];

    /// <summary>Gets the resource name normalized for cloud providers (lowercase, hyphenated).</summary>
    public string NormalizedName => NormalizeName(ComputeResource.Name);

    /// <summary>
    /// Processes the compute resource: collects environment variables, arguments, and endpoints, builds the
    /// provider-specific Pulumi resource, registers it, and applies customization annotations.
    /// </summary>
    public async Task<PulumiResource> ProcessResourceAsync()
    {
        await ProcessEnvironmentVariablesAsync().ConfigureAwait(false);
        await ProcessArgumentsAsync().ConfigureAwait(false);
        ProcessEndpoints();

        var resource = await BuildComputeResourceAsync().ConfigureAwait(false);

        PublishingContext.RegisterTranslatedResource(ComputeResource, resource);
        ApplyCustomizations(resource);

        return resource;
    }

    /// <summary>Creates the provider-specific Pulumi resource. Implemented by provider packages.</summary>
    protected abstract Task<PulumiResource> BuildComputeResourceAsync();

    /// <summary>Resolves a self/cross endpoint reference to the target platform address. Implemented by providers.</summary>
    /// <param name="endpoint">The endpoint reference to resolve.</param>
    protected abstract Output<string> ResolveEndpoint(EndpointReference endpoint);

    /// <summary>Resolves a single endpoint property to the target platform value. Implemented by providers.</summary>
    /// <param name="expression">The endpoint property expression to resolve.</param>
    protected abstract Output<string> ResolveEndpointExpression(EndpointReferenceExpression expression);

    /// <summary>Processes endpoints from the resource. Providers override to build their endpoint mappings.</summary>
    protected virtual void ProcessEndpoints()
    {
    }

    /// <summary>
    /// Resolves an Aspire structured value to a Pulumi <see cref="Output{T}"/>, tracking whether the value is
    /// secret. Mirrors the value handling performed by Aspire's Azure Container Apps translator.
    /// </summary>
    /// <param name="value">The value object from an environment variable, argument, or reference expression.</param>
    protected async Task<PulumiResolvedValue> ResolveValueAsync(object? value)
    {
        switch (value)
        {
            case null:
                return new(Output.Create(string.Empty), IsSecret: false);

            case string s:
                return new(Output.Create(s), IsSecret: false);

            case ParameterResource parameter:
            {
                var resolved = await parameter.GetValueAsync(CancellationToken).ConfigureAwait(false) ?? string.Empty;
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
                var resolved = await ((IValueProvider)connectionString).GetValueAsync(CancellationToken).ConfigureAwait(false) ?? string.Empty;
                return new(Output.CreateSecret(resolved), IsSecret: true);
            }

            case IResourceWithConnectionString resourceWithConnectionString:
            {
                var resolved = await resourceWithConnectionString.GetValueAsync(CancellationToken).ConfigureAwait(false) ?? string.Empty;
                return new(Output.CreateSecret(resolved), IsSecret: true);
            }

            case PulumiOutputReference outputReference:
            {
                // The reference resolves to its deferred string value, which the environment populates from the
                // deployed stack outputs. Output references are not secret-bearing on their own.
                var resolved = await outputReference.GetValueAsync(CancellationToken).ConfigureAwait(false) ?? string.Empty;
                return new(Output.Create(resolved), IsSecret: false);
            }

            case ReferenceExpression referenceExpression:
                return await ResolveReferenceExpressionAsync(referenceExpression).ConfigureAwait(false);

            case IValueProvider valueProvider:
            {
                var resolved = await valueProvider.GetValueAsync(
                    new ValueProviderContext { ExecutionContext = ExecutionContext },
                    CancellationToken).ConfigureAwait(false) ?? string.Empty;
                return new(Output.Create(resolved), IsSecret: false);
            }

            case IManifestExpressionProvider manifestExpression:
                // No resolver available for a bare manifest expression here; surface its expression text.
                return new(Output.Create(manifestExpression.ValueExpression), IsSecret: false);

            default:
                return new(Output.Create(value.ToString() ?? string.Empty), IsSecret: false);
        }
    }

    private async Task<PulumiResolvedValue> ResolveReferenceExpressionAsync(ReferenceExpression expression)
    {
        // Simple single-provider passthrough keeps the underlying value's secret-ness and Output identity.
        if (expression.Format == "{0}" && expression.ValueProviders.Count == 1)
        {
            return await ResolveValueAsync(expression.ValueProviders[0]).ConfigureAwait(false);
        }

        var parts = new Output<string>[expression.ValueProviders.Count];
        var anySecret = false;

        for (var i = 0; i < expression.ValueProviders.Count; i++)
        {
            var resolved = await ResolveValueAsync(expression.ValueProviders[i]).ConfigureAwait(false);
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

    /// <summary>Collects environment variables by invoking the resource's environment callbacks.</summary>
    protected virtual async Task ProcessEnvironmentVariablesAsync()
    {
        if (!ComputeResource.TryGetAnnotationsOfType<EnvironmentCallbackAnnotation>(out var annotations))
        {
            return;
        }

        var context = new EnvironmentCallbackContext(
            ExecutionContext,
            ComputeResource,
            EnvironmentVariables,
            cancellationToken: CancellationToken);

        foreach (var annotation in annotations)
        {
            await annotation.Callback(context).ConfigureAwait(false);
        }
    }

    /// <summary>Collects command-line arguments by invoking the resource's argument callbacks.</summary>
    protected virtual async Task ProcessArgumentsAsync()
    {
        if (!ComputeResource.TryGetAnnotationsOfType<CommandLineArgsCallbackAnnotation>(out var annotations))
        {
            return;
        }

        var context = new CommandLineArgsCallbackContext(Args, ComputeResource, cancellationToken: CancellationToken)
        {
            ExecutionContext = ExecutionContext,
        };

        foreach (var annotation in annotations)
        {
            await annotation.Callback(context).ConfigureAwait(false);
        }
    }

    /// <summary>Applies <see cref="PulumiCustomizationAnnotation"/> callbacks to the created Pulumi resource.</summary>
    /// <param name="resource">The created Pulumi resource.</param>
    protected void ApplyCustomizations(PulumiResource resource)
    {
        if (ComputeResource.TryGetAnnotationsOfType<PulumiCustomizationAnnotation>(out var annotations))
        {
            foreach (var annotation in annotations)
            {
                annotation.Configure(resource, PublishingContext);
            }
        }
    }

    /// <summary>
    /// Gets the fully qualified container image that was pushed to the registry for this resource, accounting
    /// for generated Dockerfiles (e.g. static-file publishing). Returns <see langword="null"/> when unavailable.
    /// </summary>
    protected async Task<string?> GetPushedContainerImageAsync()
    {
        IValueProvider imageReference = new ContainerImageReference(ComputeResource);
        return await imageReference.GetValueAsync(
            new ValueProviderContext { ExecutionContext = ExecutionContext },
            CancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gets the replica count for the resource, defaulting to 1.</summary>
    protected int GetReplicaCount() => ComputeResource.GetReplicaCount();

    /// <summary>Gets the container entrypoint if the resource is a container with a custom entrypoint.</summary>
    protected string? GetContainerEntrypoint() =>
        ComputeResource is ContainerResource container ? container.Entrypoint : null;

    /// <summary>Validates that the given endpoints only use supported URI schemes.</summary>
    /// <param name="endpoints">The endpoints to validate.</param>
    /// <param name="supportedSchemes">The supported URI schemes.</param>
    /// <exception cref="NotSupportedException">Thrown when an endpoint uses an unsupported scheme.</exception>
    protected static void ValidateEndpointSchemes(
        IEnumerable<EndpointAnnotation> endpoints,
        params string[] supportedSchemes)
    {
        var unsupported = endpoints
            .Where(e => !supportedSchemes.Contains(e.UriScheme))
            .Select(e => e.Name)
            .ToList();

        if (unsupported.Count > 0)
        {
            throw new NotSupportedException(
                $"The endpoint(s) {string.Join(", ", unsupported.Select(n => $"'{n}'"))} specify an unsupported scheme. " +
                $"The supported schemes are: {string.Join(", ", supportedSchemes.Select(s => $"'{s}'"))}.");
        }
    }

    /// <summary>Normalizes a resource name for use in cloud providers (lowercase, hyphenated).</summary>
    /// <param name="name">The original name.</param>
    protected static string NormalizeName(string name) => name.ToLowerInvariant().Replace("_", "-");
}

/// <summary>A resolved Pulumi value plus whether it should be treated as a secret.</summary>
/// <param name="Value">The resolved Pulumi output.</param>
/// <param name="IsSecret">Whether the value contains secret material.</param>
public readonly record struct PulumiResolvedValue(Output<string> Value, bool IsSecret);
