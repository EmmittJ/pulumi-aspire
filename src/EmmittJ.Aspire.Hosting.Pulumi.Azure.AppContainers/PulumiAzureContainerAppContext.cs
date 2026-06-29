// Licensed under the MIT License.

using Aspire.Hosting.ApplicationModel;
using EmmittJ.Aspire.Hosting.Pulumi;
using Microsoft.Extensions.Logging;
using Pulumi;
using Pulumi.AzureNative.App;
using Pulumi.AzureNative.App.Inputs;
using PulumiResource = Pulumi.Resource;

namespace EmmittJ.Aspire.Hosting.Pulumi.Azure.AppContainers;

/// <summary>
/// Translates a single Aspire compute resource into an Azure <see cref="ContainerApp"/>.
/// </summary>
public sealed class PulumiAzureContainerAppContext : PulumiComputeResourceContext
{
    private readonly PulumiAzureContainerAppEnvironmentContext _environmentContext;
    private readonly PulumiAzureContainerAppEnvironmentResource _environment;

    private bool _endpointsProcessed;
    private (int? Port, bool Http2, bool External)? _httpIngress;
    private readonly Dictionary<string, EndpointMapping> _endpointMapping = [];

    private readonly record struct EndpointMapping(
        string Scheme, string Host, int Port, int? TargetPort, bool IsHttpIngress, bool External, bool TlsEnabled);

    /// <summary>
    /// Initializes a new instance of the <see cref="PulumiAzureContainerAppContext"/> class.
    /// </summary>
    /// <param name="computeResource">The source Aspire compute resource.</param>
    /// <param name="environmentContext">The per-deploy environment context that owns sibling contexts.</param>
    internal PulumiAzureContainerAppContext(
        IComputeResource computeResource,
        PulumiAzureContainerAppEnvironmentContext environmentContext)
        : base(computeResource, environmentContext.PublishingContext)
    {
        _environmentContext = environmentContext;
        _environment = environmentContext.Environment;
    }

    /// <summary>
    /// Ensures this resource's endpoint mappings have been computed. Safe to call multiple times; only the
    /// first call does work. Used so a sibling can resolve this resource's addressing before it is built.
    /// </summary>
    internal void EnsureEndpointsProcessed() => ProcessEndpoints();

    /// <summary>Gets the endpoint mapping for a named endpoint, if this resource exposes it.</summary>
    private bool TryGetEndpointMapping(string endpointName, out EndpointMapping mapping) =>
        _endpointMapping.TryGetValue(endpointName, out mapping);

    /// <inheritdoc />
    protected override void ProcessEndpoints()
    {
        // Idempotent: mappings may be computed eagerly (so siblings can reference this resource) and again
        // during ProcessResourceAsync. Only compute them once.
        if (_endpointsProcessed)
        {
            return;
        }

        _endpointsProcessed = true;

        if (!ComputeResource.TryGetEndpoints(out var endpoints))
        {
            return;
        }

        var endpointList = endpoints.ToList();
        if (endpointList.Count == 0)
        {
            return;
        }

        ValidateEndpointSchemes(endpointList, "http", "https", "tcp");

        // Resolve ports from the annotations (allocated endpoints are not available in publish/deploy).
        var resolvedEndpoints = endpointList.Select(endpoint =>
        {
            var targetPort = endpoint.TargetPort ?? endpoint.Port ?? (ComputeResource is ProjectResource ? 8080 : 80);
            var exposedPort = endpoint.Port ?? targetPort;
            return (Endpoint: endpoint, TargetPort: targetPort, ExposedPort: exposedPort);
        }).ToList();

        var endpointsByTargetPort = resolvedEndpoints
            .Select((resolved, index) => (resolved, index))
            .GroupBy(x => x.resolved.TargetPort)
            .Select(g => new
            {
                Port = g.Key,
                ResolvedEndpoints = g.Select(x => x.resolved).ToArray(),
                External = g.Any(x => x.resolved.Endpoint.IsExternal),
                IsHttpOnly = g.All(x => x.resolved.Endpoint.UriScheme is "http" or "https"),
                AnyH2 = g.Any(x => x.resolved.Endpoint.Transport is "http2"),
                Index = g.Min(x => x.index),
            })
            .ToList();

        if (endpointsByTargetPort.Count(g => g.External) > 1)
        {
            throw new NotSupportedException("Multiple external endpoints are not supported by Azure Container Apps.");
        }

        if (endpointsByTargetPort.Any(g => g.External && !g.IsHttpOnly))
        {
            throw new NotSupportedException("External non-HTTP(s) endpoints are not supported by Azure Container Apps.");
        }

        var httpOnlyEndpoints = endpointsByTargetPort.Where(g => g.IsHttpOnly).OrderBy(g => g.Index).ToArray();
        var httpIngress = httpOnlyEndpoints.Length == 1 ? httpOnlyEndpoints[0] : null;

        if (httpIngress is null && httpOnlyEndpoints.Length > 0)
        {
            var externalHttp = httpOnlyEndpoints.Where(g => g.External).ToArray();
            httpIngress = externalHttp.Length == 1 ? externalHttp[0] : httpOnlyEndpoints[0];
        }

        if (httpIngress is not null)
        {
            endpointsByTargetPort.Remove(httpIngress);

            var targetPort = httpIngress.Port;
            _httpIngress = (targetPort, httpIngress.AnyH2, httpIngress.External);

            foreach (var resolved in httpIngress.ResolvedEndpoints)
            {
                var endpoint = resolved.Endpoint;
                var port = endpoint.UriScheme is "http" ? 80 : 443;
                // HTTP ingress is served over HTTPS by Container Apps regardless of the declared scheme.
                _endpointMapping[endpoint.Name] = new(endpoint.UriScheme, NormalizedName, port, targetPort, IsHttpIngress: true, httpIngress.External, TlsEnabled: true);
            }
        }

        foreach (var g in endpointsByTargetPort)
        {
            foreach (var resolved in g.ResolvedEndpoints)
            {
                var endpoint = resolved.Endpoint;
                _endpointMapping[endpoint.Name] = new(endpoint.UriScheme, NormalizedName, resolved.ExposedPort, g.Port, IsHttpIngress: false, g.External, TlsEnabled: endpoint.UriScheme is "https");
            }
        }
    }

    /// <inheritdoc />
    protected override async Task<PulumiResource> BuildComputeResourceAsync()
    {
        var appName = NormalizedName;
        var image = await GetPushedContainerImageAsync().ConfigureAwait(false) ?? $"{appName}:latest";

        var envVars = new List<EnvironmentVarArgs>();
        foreach (var (name, value) in EnvironmentVariables)
        {
            var resolved = await ResolveValueAsync(value).ConfigureAwait(false);
            envVars.Add(new EnvironmentVarArgs { Name = name, Value = resolved.Value });
        }

        var container = new ContainerArgs
        {
            Name = appName,
            Image = image,
            Resources = new ContainerResourcesArgs { Cpu = 0.5, Memory = "1Gi" },
            Env = envVars,
        };

        if (GetContainerEntrypoint() is { } entrypoint)
        {
            container.Command = new[] { entrypoint };
        }

        if (Args.Count > 0)
        {
            var argOutputs = new List<Output<string>>(Args.Count);
            foreach (var arg in Args)
            {
                argOutputs.Add((await ResolveValueAsync(arg).ConfigureAwait(false)).Value);
            }

            container.Args = Output.All(argOutputs.ToArray());
        }

        var configuration = new ConfigurationArgs();

        if (_httpIngress is { } ingressInfo)
        {
            configuration.Ingress = new IngressArgs
            {
                External = ingressInfo.External,
                TargetPort = ingressInfo.Port ?? 8080,
                Transport = ingressInfo.Http2 ? IngressTransportMethod.Http2 : IngressTransportMethod.Auto,
            };
        }

        if (_environment.Registry.ResolvedEndpoint is { } registryEndpoint &&
            _environment.ManagedIdentity is { } registryIdentity)
        {
            configuration.Registries = new[]
            {
                new RegistryCredentialsArgs { Server = registryEndpoint, Identity = registryIdentity.Id },
            };
        }

        var containerAppArgs = new ContainerAppArgs
        {
            ContainerAppName = appName,
            ResourceGroupName = _environment.ResourceGroup!.Name,
            Location = _environment.Location,
            ManagedEnvironmentId = _environment.ManagedEnvironment!.Id,
            Configuration = configuration,
            Template = new TemplateArgs
            {
                Scale = new ScaleArgs { MinReplicas = GetReplicaCount(), MaxReplicas = 3 },
                Containers = new[] { container },
            },
        };

        if (_environment.ManagedIdentity is { } identity)
        {
            containerAppArgs.Identity = new ManagedServiceIdentityArgs
            {
                Type = ManagedServiceIdentityType.UserAssigned,
                UserAssignedIdentities = new[] { identity.Id },
            };
        }

        var containerApp = new ContainerApp(appName, containerAppArgs);

        // Export the app FQDN so the per-resource print-summary step can surface a URL.
        PublishingContext.AddOutput($"{appName}-fqdn", containerApp.LatestRevisionFqdn);

        return containerApp;
    }

    /// <inheritdoc />
    protected override Output<string> ResolveEndpoint(EndpointReference endpoint) =>
        ResolveEndpointProperty(endpoint, EndpointProperty.Url);

    /// <inheritdoc />
    protected override Output<string> ResolveEndpointExpression(EndpointReferenceExpression expression) =>
        ResolveEndpointProperty(expression.Endpoint, expression.Property);

    /// <summary>
    /// Resolves an endpoint property for the resource that owns the endpoint, which may be this resource or a
    /// sibling deployed to the same environment. Falls back to a best-effort host when the owner is not a
    /// compute resource targeted to this environment (for example a cross-environment reference).
    /// </summary>
    private Output<string> ResolveEndpointProperty(EndpointReference endpoint, EndpointProperty property)
    {
        // Look up the context for the resource that owns the endpoint. For a sibling this reads its
        // mappings, which were computed eagerly when the environment registered every targeted resource.
        var ownerContext = IsSelf(endpoint.Resource)
            ? this
            : _environmentContext.TryGetContext(endpoint.Resource);

        if (ownerContext is not null)
        {
            ownerContext.EnsureEndpointsProcessed();
            if (ownerContext.TryGetEndpointMapping(endpoint.EndpointName, out var mapping))
            {
                // DefaultDomain is shared across the environment, so any context can compose the value.
                return GetEndpointValue(mapping, property);
            }
        }

        return FallbackEndpointValue(endpoint, property);
    }

    /// <summary>
    /// Composes an endpoint property value from a mapping, mirroring Azure Container Apps' addressing:
    /// external HTTP ingress resolves to <c>{app}.{defaultDomain}</c>, internal HTTP ingress to
    /// <c>{app}.internal.{defaultDomain}</c>, and non-HTTP endpoints to the app's internal DNS name.
    /// </summary>
    private Output<string> GetEndpointValue(EndpointMapping mapping, EndpointProperty property)
    {
        var (scheme, host, port, targetPort, isHttpIngress, external, tlsEnabled) = mapping;

        // The default domain is only known after the managed environment is provisioned, so HTTP-ingress
        // hosts must be composed lazily from the Output rather than as a literal string.
        Output<string> GetHost(string prefix = "", string suffix = "")
        {
            if (isHttpIngress)
            {
                return _environmentContext.DefaultDomain.Apply(domain =>
                    $"{prefix}{BuildFqdnHost(host, httpIngress: true, external, domain)}{suffix}");
            }

            return Output.Create($"{prefix}{host}{suffix}");
        }

        return property switch
        {
            EndpointProperty.Url => GetHost($"{scheme}://", isHttpIngress ? "" : $":{port}"),
            EndpointProperty.Host or EndpointProperty.IPV4Host => GetHost(),
            EndpointProperty.Port => Output.Create(port.ToString()),
            EndpointProperty.HostAndPort => GetHost(suffix: $":{port}"),
            EndpointProperty.TargetPort => Output.Create(targetPort?.ToString() ?? "8080"),
            EndpointProperty.Scheme => Output.Create(scheme),
            EndpointProperty.TlsEnabled => Output.Create(tlsEnabled ? "true" : "false"),
            _ => throw new NotSupportedException($"Endpoint property '{property}' is not supported."),
        };
    }

    /// <summary>
    /// Builds the host portion of an Azure Container Apps endpoint. This is a pure function so the FQDN
    /// formula can be unit-tested without a Pulumi engine.
    /// </summary>
    /// <param name="appName">The normalized container app name.</param>
    /// <param name="httpIngress">Whether the endpoint is served through HTTP ingress.</param>
    /// <param name="external">Whether the ingress is external (public) or internal.</param>
    /// <param name="defaultDomain">The managed environment's default domain.</param>
    internal static string BuildFqdnHost(string appName, bool httpIngress, bool external, string defaultDomain)
    {
        if (!httpIngress)
        {
            // Non-HTTP (TCP) endpoints are reachable by the app's internal DNS name within the environment.
            return appName;
        }

        return external
            ? $"{appName}.{defaultDomain}"
            : $"{appName}.internal.{defaultDomain}";
    }

    private Output<string> FallbackEndpointValue(EndpointReference endpoint, EndpointProperty property)
    {
        // The endpoint owner is not a compute resource in this environment (e.g. a cross-environment
        // reference). Cross-environment endpoint resolution is not implemented; use a best-effort host.
        var host = _environment.GetHostAddressExpression(endpoint);

        return property switch
        {
            EndpointProperty.Url => Output.Create($"{endpoint.Scheme}://{host.Format}"),
            EndpointProperty.Host or EndpointProperty.IPV4Host => Output.Create(host.Format),
            EndpointProperty.Port or EndpointProperty.TargetPort => Output.Create(endpoint.TargetPort?.ToString() ?? "8080"),
            EndpointProperty.HostAndPort => Output.Create($"{host.Format}:{endpoint.TargetPort?.ToString() ?? "8080"}"),
            EndpointProperty.Scheme => Output.Create(endpoint.Scheme),
            EndpointProperty.TlsEnabled => Output.Create(endpoint.Scheme is "https" ? "true" : "false"),
            _ => Output.Create(string.Empty),
        };
    }

    private bool IsSelf(IResource resource) =>
        ReferenceEquals(resource, ComputeResource) ||
        string.Equals(resource.Name, ComputeResource.Name, StringComparison.OrdinalIgnoreCase);
}
