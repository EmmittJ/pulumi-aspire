// Licensed under the Apache License, Version 2.0.

using Aspire.Hosting.ApplicationModel;
using EmmittJ.Aspire.Hosting.Pulumi;
using Microsoft.Extensions.Logging;
using Pulumi;
using Pulumi.AzureNative.App;
using Pulumi.AzureNative.App.Inputs;
using PulumiResource = Pulumi.Resource;

namespace EmmittJ.Aspire.Hosting.Pulumi.Azure;

/// <summary>
/// Translates a single Aspire compute resource into an Azure <see cref="ContainerApp"/>.
/// </summary>
public sealed class PulumiAzureComputeResourceContext : PulumiComputeResourceContext
{
    private readonly PulumiAzureEnvironmentResource _environment;

    private (int? Port, bool Http2, bool External)? _httpIngress;
    private readonly Dictionary<string, EndpointMapping> _endpointMapping = [];

    private readonly record struct EndpointMapping(string Scheme, string Host, int Port, int? TargetPort, bool IsHttpIngress, bool External);

    /// <summary>
    /// Initializes a new instance of the <see cref="PulumiAzureComputeResourceContext"/> class.
    /// </summary>
    /// <param name="computeResource">The source Aspire compute resource.</param>
    /// <param name="publishingContext">The publishing context.</param>
    /// <param name="environment">The Azure environment resource.</param>
    public PulumiAzureComputeResourceContext(
        IComputeResource computeResource,
        PulumiPublishingContext publishingContext,
        PulumiAzureEnvironmentResource environment)
        : base(computeResource, publishingContext)
    {
        _environment = environment;
    }

    /// <inheritdoc />
    protected override void ProcessEndpoints()
    {
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
                _endpointMapping[endpoint.Name] = new(endpoint.UriScheme, NormalizedName, port, targetPort, IsHttpIngress: true, httpIngress.External);
            }
        }

        foreach (var g in endpointsByTargetPort)
        {
            foreach (var resolved in g.ResolvedEndpoints)
            {
                var endpoint = resolved.Endpoint;
                _endpointMapping[endpoint.Name] = new(endpoint.UriScheme, NormalizedName, resolved.ExposedPort, g.Port, IsHttpIngress: false, g.External);
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
    protected override Output<string> ResolveEndpoint(EndpointReference endpoint)
    {
        if (IsSelf(endpoint.Resource) && _endpointMapping.TryGetValue(endpoint.EndpointName, out var mapping))
        {
            return Output.Create($"{mapping.Scheme}://{mapping.Host}");
        }

        var host = _environment.GetHostAddressExpression(endpoint);
        return Output.Create($"{endpoint.Scheme}://{host.Format}");
    }

    /// <inheritdoc />
    protected override Output<string> ResolveEndpointExpression(EndpointReferenceExpression expression)
    {
        var endpoint = expression.Endpoint;

        if (IsSelf(endpoint.Resource) && _endpointMapping.TryGetValue(endpoint.EndpointName, out var mapping))
        {
            return expression.Property switch
            {
                EndpointProperty.Url => mapping.IsHttpIngress
                    ? Output.Create($"{mapping.Scheme}://{mapping.Host}")
                    : Output.Create($"{mapping.Scheme}://{mapping.Host}:{mapping.Port}"),
                EndpointProperty.Host or EndpointProperty.IPV4Host => Output.Create(mapping.Host),
                EndpointProperty.Port => Output.Create(mapping.Port.ToString()),
                EndpointProperty.TargetPort => Output.Create(mapping.TargetPort?.ToString() ?? "8080"),
                EndpointProperty.HostAndPort => Output.Create($"{mapping.Host}:{mapping.Port}"),
                EndpointProperty.Scheme => Output.Create(mapping.Scheme),
                EndpointProperty.TlsEnabled => Output.Create(mapping.Scheme is "https" ? "true" : "false"),
                _ => throw new NotSupportedException($"Endpoint property '{expression.Property}' is not supported."),
            };
        }

        return expression.Property switch
        {
            EndpointProperty.Url => ResolveEndpoint(endpoint),
            EndpointProperty.Host or EndpointProperty.IPV4Host => Output.Create(endpoint.Resource.Name.ToLowerInvariant()),
            EndpointProperty.Port or EndpointProperty.TargetPort => Output.Create(endpoint.TargetPort?.ToString() ?? "8080"),
            EndpointProperty.HostAndPort => Output.Create($"{endpoint.Resource.Name.ToLowerInvariant()}:{endpoint.TargetPort?.ToString() ?? "8080"}"),
            EndpointProperty.Scheme => Output.Create(endpoint.Scheme),
            EndpointProperty.TlsEnabled => Output.Create(endpoint.Scheme is "https" ? "true" : "false"),
            _ => Output.Create(string.Empty),
        };
    }

    private bool IsSelf(IResource resource) =>
        ReferenceEquals(resource, ComputeResource) ||
        string.Equals(resource.Name, ComputeResource.Name, StringComparison.OrdinalIgnoreCase);
}
