// Licensed under the Apache License, Version 2.0.

#pragma warning disable ASPIRECOMPUTE002 // Compute environment is experimental
#pragma warning disable ASPIREPIPELINES003 // ContainerImageReference is experimental

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.Logging;
using Pulumi;
using PulumiResource = Pulumi.Resource;

namespace EmmittJ.Aspire.Hosting.Pulumi;

/// <summary>
/// Abstract base class for processing Aspire compute resources into Pulumi resources.
/// Provider-specific implementations handle the actual resource creation.
/// </summary>
/// <remarks>
/// <para>
/// This class provides common functionality for processing environment variables,
/// command-line arguments, endpoints, and other compute resource properties.
/// </para>
/// <para>
/// Provider packages (Azure, AWS, Kubernetes) implement the abstract
/// <see cref="BuildComputeResourceAsync"/> method to create provider-specific resources.
/// </para>
/// </remarks>
public abstract class PulumiComputeResourceContext
{
    private readonly List<EndpointInfo> _endpoints = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="PulumiComputeResourceContext"/> class.
    /// </summary>
    /// <param name="computeResource">The source Aspire compute resource.</param>
    /// <param name="publishingContext">The publishing context.</param>
    protected PulumiComputeResourceContext(
        IComputeResource computeResource,
        PulumiPublishingContext publishingContext)
    {
        ComputeResource = computeResource;
        PublishingContext = publishingContext;
    }

    /// <summary>
    /// Gets the source Aspire compute resource.
    /// </summary>
    public IComputeResource ComputeResource { get; }

    /// <summary>
    /// Gets the publishing context.
    /// </summary>
    public PulumiPublishingContext PublishingContext { get; }

    /// <summary>
    /// Gets the execution context.
    /// </summary>
    public DistributedApplicationExecutionContext ExecutionContext => PublishingContext.ExecutionContext;

    /// <summary>
    /// Gets the logger instance.
    /// </summary>
    protected ILogger Logger => PublishingContext.Logger;

    /// <summary>
    /// Gets the resolved environment variables for the resource.
    /// Keys are environment variable names, values are the raw value objects
    /// (strings, <see cref="EndpointReference"/>, <see cref="ParameterResource"/>, etc.).
    /// Resolution happens in <see cref="BuildComputeResourceAsync"/>.
    /// </summary>
    public Dictionary<string, object> EnvironmentVariables { get; } = [];

    /// <summary>
    /// Gets the command-line arguments for the resource.
    /// Values are raw value objects that may need resolution.
    /// </summary>
    public List<object> Args { get; } = [];

    /// <summary>
    /// Gets the collected endpoints.
    /// </summary>
    public IReadOnlyList<EndpointInfo> Endpoints => _endpoints;

    /// <summary>
    /// Gets the normalized resource name (lowercase, alphanumeric with hyphens).
    /// </summary>
    public string NormalizedName => NormalizeName(ComputeResource.Name);

    /// <summary>
    /// Processes the compute resource and creates the provider-specific Pulumi resources.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The created Pulumi resource.</returns>
    public async Task<PulumiResource> ProcessResourceAsync(CancellationToken cancellationToken = default)
    {
        Logger.LogDebug("Processing compute resource '{Name}'", ComputeResource.Name);

        // Process common aspects
        await ProcessEnvironmentVariablesAsync(cancellationToken).ConfigureAwait(false);
        await ProcessArgumentsAsync(cancellationToken).ConfigureAwait(false);
        ProcessEndpoints();

        // Let the provider-specific implementation create the actual resource
        var resource = await BuildComputeResourceAsync();

        // Register the translated resource
        PublishingContext.RegisterResource(ComputeResource, resource);

        // Apply customization annotations
        await ApplyCustomizationsAsync(resource);

        Logger.LogInformation(
            "Created Pulumi resource for '{Name}'",
            ComputeResource.Name);

        return resource;
    }

    /// <summary>
    /// Creates the provider-specific Pulumi resource.
    /// Implemented by provider packages (Azure, AWS, Kubernetes).
    /// </summary>
    /// <returns>The created Pulumi resource.</returns>
    protected abstract Task<PulumiResource> BuildComputeResourceAsync();

    /// <summary>
    /// Processes environment variables from the compute resource by invoking callbacks.
    /// Stores raw values in <see cref="EnvironmentVariables"/> for later resolution.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    protected virtual async Task ProcessEnvironmentVariablesAsync(CancellationToken cancellationToken = default)
    {
        // Environment variables are collected via EnvironmentCallbackAnnotation
        if (!ComputeResource.TryGetAnnotationsOfType<EnvironmentCallbackAnnotation>(out var annotations))
        {
            return;
        }

        var context = new EnvironmentCallbackContext(
            ExecutionContext,
            ComputeResource,
            EnvironmentVariables,
            cancellationToken: cancellationToken);

        foreach (var callback in annotations)
        {
            await callback.Callback(context).ConfigureAwait(false);
        }

        Logger.LogDebug(
            "Collected {Count} environment variables for '{Name}'",
            EnvironmentVariables.Count,
            ComputeResource.Name);
    }

    /// <summary>
    /// Processes command-line arguments from the compute resource.
    /// Stores raw values in <see cref="Args"/> for later resolution.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    protected virtual async Task ProcessArgumentsAsync(CancellationToken cancellationToken = default)
    {
        // Arguments are collected via CommandLineArgsCallbackAnnotation
        if (!ComputeResource.TryGetAnnotationsOfType<CommandLineArgsCallbackAnnotation>(out var annotations))
        {
            return;
        }

        var context = new CommandLineArgsCallbackContext(Args, ComputeResource, cancellationToken: cancellationToken)
        {
            ExecutionContext = ExecutionContext,
        };

        foreach (var callback in annotations)
        {
            await callback.Callback(context).ConfigureAwait(false);
        }

        Logger.LogDebug(
            "Collected {Count} command-line arguments for '{Name}'",
            Args.Count,
            ComputeResource.Name);
    }

    /// <summary>
    /// Processes endpoints from the compute resource using EndpointAnnotation.
    /// This method uses annotations directly rather than allocated endpoints,
    /// which are not available in publish mode.
    /// </summary>
    protected virtual void ProcessEndpoints()
    {
        // Use TryGetEndpoints which returns EndpointAnnotation objects
        // This works in publish mode without requiring allocated endpoints
        if (!ComputeResource.TryGetEndpoints(out var endpoints))
        {
            return;
        }

        foreach (var endpoint in endpoints)
        {
            // Get ports from the annotation (not allocated endpoint)
            var targetPort = endpoint.TargetPort ?? endpoint.Port ?? 8080;
            var exposedPort = endpoint.Port ?? targetPort;

            var info = new EndpointInfo(
                endpoint.Name,
                endpoint.UriScheme,
                exposedPort,
                targetPort,
                endpoint.IsExternal);

            _endpoints.Add(info);

            Logger.LogDebug(
                "Found endpoint '{EndpointName}' ({Scheme}:{Port}) for '{Name}'",
                endpoint.Name,
                endpoint.UriScheme,
                exposedPort,
                ComputeResource.Name);
        }
    }

    /// <summary>
    /// Applies customization annotations to the created resource.
    /// </summary>
    /// <param name="resource">The created Pulumi resource.</param>
    protected virtual Task ApplyCustomizationsAsync(PulumiResource resource)
    {
        // Check for generic customization annotations
        if (ComputeResource.TryGetAnnotationsOfType<PulumiCustomizationAnnotation>(out var annotations))
        {
            foreach (var annotation in annotations)
            {
                annotation.Configure(resource, PublishingContext);
                Logger.LogDebug(
                    "Applied customization annotation to '{Name}'",
                    ComputeResource.Name);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Normalizes a resource name for use in cloud providers.
    /// </summary>
    /// <param name="name">The original name.</param>
    /// <returns>A normalized name (lowercase, alphanumeric with hyphens).</returns>
    protected static string NormalizeName(string name)
    {
        return name.ToLowerInvariant().Replace("_", "-");
    }

    /// <summary>
    /// Gets the container image for this compute resource.
    /// </summary>
    /// <returns>The container image string, or null if not available.</returns>
    protected virtual string? GetContainerImage()
    {
        if (!ComputeResource.TryGetAnnotationsOfType<ContainerImageAnnotation>(out var annotations))
        {
            return null;
        }

        var imageAnnotation = annotations.FirstOrDefault();
        if (imageAnnotation is null)
        {
            return null;
        }

        var registry = imageAnnotation.Registry;
        var image = imageAnnotation.Image;
        var tag = imageAnnotation.Tag ?? "latest";

        return string.IsNullOrEmpty(registry)
            ? $"{image}:{tag}"
            : $"{registry}/{image}:{tag}";
    }

    /// <summary>
    /// Gets the pushed container image for this compute resource asynchronously.
    /// This returns the fully qualified image name that was pushed to the container registry,
    /// which may differ from the original image annotation (e.g., after PublishWithStaticFiles
    /// creates a custom Dockerfile).
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The pushed container image string, or null if not available.</returns>
    protected virtual async Task<string?> GetPushedContainerImageAsync(CancellationToken cancellationToken = default)
    {
        // Use ContainerImageReference to get the fully qualified pushed image name
        // This handles the case where a custom Dockerfile is generated (e.g., PublishWithStaticFiles)
        // and the image is pushed to the container registry with a different name than the original annotation
        IValueProvider cir = new ContainerImageReference(ComputeResource);
        var valueProviderContext = new ValueProviderContext
        {
            ExecutionContext = ExecutionContext
        };

        var pushedImage = await cir.GetValueAsync(valueProviderContext, cancellationToken).ConfigureAwait(false);

        Logger.LogDebug(
            "Resolved pushed container image for '{Name}': {Image}",
            ComputeResource.Name,
            pushedImage ?? "(null)");

        return pushedImage;
    }

    /// <summary>
    /// Gets the replica count for the resource.
    /// </summary>
    /// <returns>The replica count, defaulting to 1 if not specified.</returns>
    protected int GetReplicaCount()
    {
        return ComputeResource.GetReplicaCount();
    }

    /// <summary>
    /// Gets the container entrypoint if the resource is a container with a custom entrypoint.
    /// </summary>
    /// <returns>The entrypoint command, or <see langword="null"/> if not applicable.</returns>
    protected string? GetContainerEntrypoint()
    {
        return ComputeResource is ContainerResource container ? container.Entrypoint : null;
    }

    /// <summary>
    /// Validates that endpoints use supported schemes.
    /// </summary>
    /// <param name="endpoints">The endpoints to validate.</param>
    /// <param name="supportedSchemes">The supported URI schemes (e.g., "http", "https", "tcp").</param>
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
                $"The endpoint(s) {string.Join(", ", unsupported.Select(n => $"'{n}'"))} " +
                $"specify an unsupported scheme. The supported schemes are: {string.Join(", ", supportedSchemes.Select(s => $"'{s}'"))}.");
        }
    }
}

/// <summary>
/// Information about an endpoint on a compute resource.
/// </summary>
/// <param name="Name">The endpoint name.</param>
/// <param name="Scheme">The endpoint scheme (http, https, tcp, etc.).</param>
/// <param name="Port">The exposed port.</param>
/// <param name="TargetPort">The target port on the container.</param>
/// <param name="IsExternal">Whether the endpoint is externally accessible.</param>
public sealed record EndpointInfo(
    string Name,
    string Scheme,
    int? Port,
    int? TargetPort,
    bool IsExternal);
