// Licensed under the Apache License, Version 2.0.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;
using Pulumi;
using Pulumi.AzureNative.App;
using Pulumi.AzureNative.App.Inputs;

namespace EmmittJ.Aspire.Hosting.Pulumi.Azure;

/// <summary>
/// Context for processing Azure compute resources.
/// Extends the base context with Azure Container Apps-specific functionality.
/// </summary>
/// <remarks>
/// <para>
/// This context provides Azure-specific resource creation for compute resources,
/// translating Aspire resources to Azure Container App resources.
/// </para>
/// </remarks>
public sealed class PulumiAzureComputeResourceContext : PulumiComputeResourceContext
{
    private readonly PulumiAzureEnvironmentResource _azureEnvironment;

    // Endpoint state after processing
    private (int? Port, bool Http2, bool External)? _httpIngress;
    private readonly List<int> _additionalPorts = [];

    private record struct EndpointMapping(string Scheme, string Host, int Port, int? TargetPort, bool IsHttpIngress, bool External);
    private readonly Dictionary<string, EndpointMapping> _endpointMapping = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="PulumiAzureComputeResourceContext"/> class.
    /// </summary>
    /// <param name="computeResource">The source Aspire compute resource.</param>
    /// <param name="publishingContext">The publishing context.</param>
    /// <param name="azureEnvironment">The Azure environment resource.</param>
    public PulumiAzureComputeResourceContext(
        IComputeResource computeResource,
        PulumiPublishingContext publishingContext,
        PulumiAzureEnvironmentResource azureEnvironment)
        : base(computeResource, publishingContext)
    {
        _azureEnvironment = azureEnvironment;
    }

    /// <inheritdoc />
    protected override void ProcessEndpoints()
    {
        // Get endpoints from the resource
        if (!ComputeResource.TryGetEndpoints(out var endpoints))
        {
            return;
        }

        var endpointList = endpoints.ToList();
        if (endpointList.Count == 0)
        {
            return;
        }

        // Validate schemes
        ValidateEndpointSchemes(endpointList, "http", "https", "tcp");

        // Resolve endpoint ports
        var resolvedEndpoints = endpointList.Select(endpoint =>
        {
            var targetPort = endpoint.TargetPort ?? endpoint.Port ?? (ComputeResource is ProjectResource ? 8080 : 80);
            var exposedPort = endpoint.Port ?? targetPort;
            return (Endpoint: endpoint, TargetPort: targetPort, ExposedPort: exposedPort);
        }).ToList();

        // Group resolved endpoints by target port
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
                UniqueSchemes = g.Select(x => x.resolved.Endpoint.UriScheme).Distinct().ToArray(),
                Index = g.Min(x => x.index)
            })
            .ToList();

        // Failure cases
        if (endpointsByTargetPort.Count(g => g.External) > 1)
        {
            throw new NotSupportedException("Multiple external endpoints are not supported");
        }

        if (endpointsByTargetPort.Any(g => g.External && !g.IsHttpOnly))
        {
            throw new NotSupportedException("External non-HTTP(s) endpoints are not supported");
        }

        // Get all http only groups
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

            // Port is already resolved - it's the target port from grouping
            var targetPort = httpIngress.Port;
            _httpIngress = (targetPort, httpIngress.AnyH2, httpIngress.External);

            foreach (var resolved in httpIngress.ResolvedEndpoints)
            {
                var endpoint = resolved.Endpoint;
                var port = endpoint.UriScheme is "http" ? 80 : 443;
                _endpointMapping[endpoint.Name] = new(endpoint.UriScheme, NormalizedName, port, targetPort, true, httpIngress.External);
            }
        }

        foreach (var g in endpointsByTargetPort)
        {
            if (g.Port is int portValue)
            {
                _additionalPorts.Add(portValue);
            }

            foreach (var resolved in g.ResolvedEndpoints)
            {
                var endpoint = resolved.Endpoint;
                _endpointMapping[endpoint.Name] = new(endpoint.UriScheme, NormalizedName, resolved.ExposedPort, g.Port, false, g.External);
            }
        }
    }

    /// <inheritdoc />
    protected override async Task<global::Pulumi.Resource> BuildComputeResourceAsync()
    {
        var appName = NormalizedName;

        // Get the pushed container image - this returns the fully qualified image that was pushed
        // to the container registry, which may differ from the original annotation (e.g., after
        // PublishWithStaticFiles creates a custom Dockerfile and pushes to ACR)
        var image = await GetPushedContainerImageAsync().ConfigureAwait(false) ?? $"{appName}:latest";

        Logger.LogDebug(
            "Using container image '{Image}' for Container App '{AppName}'",
            image, appName);

        // Build container configuration with resolved environment variables
        var envVars = new List<EnvironmentVarArgs>();
        foreach (var (name, value) in EnvironmentVariables)
        {
            var resolvedValue = ResolveValue(value);
            envVars.Add(new EnvironmentVarArgs
            {
                Name = name,
                Value = resolvedValue
            });
        }

        Logger.LogDebug(
            "Container App '{AppName}' has {EnvCount} environment variables",
            appName, envVars.Count);

        var container = new ContainerArgs
        {
            Name = appName,
            Image = image,
            Resources = new ContainerResourcesArgs
            {
                Cpu = 0.5,
                Memory = "1Gi"
            },
            Env = envVars
        };

        // Set entrypoint if container resource
        if (GetContainerEntrypoint() is { } entrypoint)
        {
            container.Command = [entrypoint];
        }

        // Add args if any
        if (Args.Count > 0)
        {
            var argsList = new List<string>();
            foreach (var arg in Args)
            {
                // For now, simple string conversion - we can enhance later
                argsList.Add(arg.ToString() ?? string.Empty);
            }
            container.Args = argsList.ToArray();
        }

        // Build ingress configuration
        IngressArgs? ingress = null;
        if (_httpIngress is { } httpIngress)
        {
            ingress = new IngressArgs
            {
                External = httpIngress.External,
                TargetPort = httpIngress.Port ?? 8080,
                Transport = httpIngress.Http2 ? IngressTransportMethod.Http2 : IngressTransportMethod.Auto
            };
        }

        // Build configuration with registry authentication using managed identity
        var configuration = new ConfigurationArgs();

        if (ingress is not null)
        {
            configuration.Ingress = ingress;
        }

        // Add registry configuration if we have a container registry with managed identity
        if (_azureEnvironment.ContainerRegistry.ResolvedEndpoint is { } registryEndpoint &&
            _azureEnvironment.ManagedIdentity is { } managedIdentity)
        {
            configuration.Registries = new[]
            {
                new RegistryCredentialsArgs
                {
                    Server = registryEndpoint,
                    Identity = managedIdentity.Id
                }
            };
        }

        // Create the Container App with identity for ACR authentication
        var containerAppArgs = new ContainerAppArgs
        {
            ContainerAppName = appName,
            ResourceGroupName = _azureEnvironment.ResourceGroup!.Name,
            Location = _azureEnvironment.Location,
            ManagedEnvironmentId = _azureEnvironment.ManagedEnvironment!.Id,
            Configuration = configuration,
            Template = new TemplateArgs
            {
                Scale = new ScaleArgs
                {
                    MinReplicas = GetReplicaCount(),
                    MaxReplicas = 3
                },
                Containers = [container]
            }
        };

        // Add user-assigned managed identity for ACR pull
        if (_azureEnvironment.ManagedIdentity is { } identity)
        {
            containerAppArgs.Identity = new ManagedServiceIdentityArgs
            {
                Type = ManagedServiceIdentityType.UserAssigned,
                UserAssignedIdentities = new[]
                {
                    identity.Id
                }
            };
        }

        // Create the Container App
        var containerApp = new ContainerApp(appName, containerAppArgs);

        // Add output for the app's FQDN
        PublishingContext.AddOutput($"{appName}-fqdn", containerApp.LatestRevisionFqdn);

        // Apply any customization annotations
        if (ComputeResource.TryGetAnnotationsOfType<PulumiAzureContainerAppCustomizationAnnotation>(out var annotations))
        {
            foreach (var annotation in annotations)
            {
                annotation.Configure(containerApp, PublishingContext);
                Logger.LogDebug(
                    "Applied Azure Container App customization to '{Name}'",
                    ComputeResource.Name);
            }
        }

        Logger.LogInformation(
            "Created Container App '{AppName}' for resource '{ResourceName}' with {EnvCount} env vars",
            appName, ComputeResource.Name, envVars.Count);

        return containerApp;
    }

    /// <summary>
    /// Resolves a value to a Pulumi Output, handling self-referencing endpoints locally.
    /// </summary>
    private Output<string> ResolveValue(object value)
    {
        return value switch
        {
            string s => Output.Create(s),
            Output<string> o => o,
            EndpointReference ep => ResolveEndpointReference(ep),
            EndpointReferenceExpression epExpr => ResolveEndpointReferenceExpression(epExpr),
            ParameterResource param => Output.Create(param.Default?.ToString() ?? param.Name),
            ReferenceExpression refExpr => ResolveReferenceExpression(refExpr),
            _ => Output.Create(value.ToString() ?? string.Empty)
        };
    }

    private Output<string> ResolveEndpointReference(EndpointReference ep)
    {
        // Check if this is a self-reference (endpoint on this resource)
        if (IsSelf(ep.Resource) && _endpointMapping.TryGetValue(ep.EndpointName, out var mapping))
        {
            return Output.Create($"{mapping.Scheme}://{mapping.Host}");
        }

        // External reference - use the environment's host address expression
        var hostExpression = _azureEnvironment.GetHostAddressExpression(ep);
        return Output.Create($"{ep.Scheme}://{hostExpression.Format}");
    }

    private Output<string> ResolveEndpointReferenceExpression(EndpointReferenceExpression epExpr)
    {
        var ep = epExpr.Endpoint;

        // Check if this is a self-reference
        if (IsSelf(ep.Resource) && _endpointMapping.TryGetValue(ep.EndpointName, out var mapping))
        {
            return epExpr.Property switch
            {
                EndpointProperty.Url => mapping.IsHttpIngress
                    ? Output.Create($"{mapping.Scheme}://{mapping.Host}")
                    : Output.Create($"{mapping.Scheme}://{mapping.Host}:{mapping.Port}"),
                EndpointProperty.Host or EndpointProperty.IPV4Host => Output.Create(mapping.Host),
                EndpointProperty.Port => Output.Create(mapping.Port.ToString()),
                EndpointProperty.TargetPort => Output.Create(mapping.TargetPort?.ToString() ?? "8080"),
                EndpointProperty.Scheme => Output.Create(mapping.Scheme),
                _ => throw new NotSupportedException($"Endpoint property {epExpr.Property} is not supported")
            };
        }

        // External reference
        return epExpr.Property switch
        {
            EndpointProperty.Url => ResolveEndpointReference(ep),
            EndpointProperty.Host or EndpointProperty.IPV4Host => Output.Create(ep.Resource.Name.ToLowerInvariant()),
            EndpointProperty.Port => Output.Create(ep.TargetPort?.ToString() ?? "8080"),
            EndpointProperty.TargetPort => Output.Create(ep.TargetPort?.ToString() ?? "8080"),
            EndpointProperty.Scheme => Output.Create(ep.Scheme),
            _ => Output.Create(string.Empty)
        };
    }

    private Output<string> ResolveReferenceExpression(ReferenceExpression refExpr)
    {
        // Handle simple expressions
        if (refExpr.Format == "{0}" && refExpr.ValueProviders.Count == 1)
        {
            return ResolveValue(refExpr.ValueProviders[0]);
        }

        // For complex expressions, resolve each part and combine
        var parts = new List<Output<string>>();
        foreach (var vp in refExpr.ValueProviders)
        {
            parts.Add(ResolveValue(vp));
        }

        // Combine parts using the format string
        return Output.All(parts).Apply(values =>
        {
            var args = values.ToArray();
            return string.Format(refExpr.Format, args);
        });
    }

    /// <summary>
    /// Determines if the given resource is the same as this container app.
    /// </summary>
    private bool IsSelf(IResource resource) =>
        resource == ComputeResource || resource.Name == ComputeResource.Name;
}
