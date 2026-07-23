// Licensed under the MIT License.

using Aspire.Hosting.ApplicationModel;
using EmmittJ.Aspire.Hosting.Pulumi;
using Pulumi;
using Pulumi.AzureNative.Web;
using Pulumi.AzureNative.Web.Inputs;
using PulumiResource = Pulumi.Resource;

namespace EmmittJ.Aspire.Hosting.Pulumi.Azure.AppService;

/// <summary>
/// Translates a single Aspire compute resource into an Azure App Service <see cref="WebApp"/>.
/// </summary>
public sealed class PulumiAzureAppServiceWebsiteContext : PulumiComputeResourceContext
{
    private readonly PulumiAzureAppServiceEnvironmentContext _environmentContext;
    private readonly PulumiAzureAppServiceEnvironmentResource _environment;

    private bool _endpointsProcessed;
    private int? _targetPort;
    private readonly Dictionary<string, EndpointMapping> _endpointMapping = [];

    private readonly record struct EndpointMapping(string Scheme, int Port, int? TargetPort, bool TlsEnabled);

    /// <summary>
    /// Initializes a new instance of the <see cref="PulumiAzureAppServiceWebsiteContext"/> class.
    /// </summary>
    /// <param name="computeResource">The source Aspire compute resource.</param>
    /// <param name="environmentContext">The per-deploy environment context that owns sibling contexts.</param>
    internal PulumiAzureAppServiceWebsiteContext(
        IComputeResource computeResource,
        PulumiAzureAppServiceEnvironmentContext environmentContext)
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

        // App Service only serves HTTP(S) through the front end; there is no raw TCP ingress like
        // Container Apps, so any other scheme cannot be represented.
        ValidateEndpointSchemes(endpointList, "http", "https");

        // Resolve ports from the annotations (allocated endpoints are not available in publish/deploy).
        var resolvedEndpoints = endpointList.Select(endpoint =>
        {
            var targetPort = endpoint.TargetPort ?? endpoint.Port ?? (ComputeResource is ProjectResource ? 8080 : 80);
            return (Endpoint: endpoint, TargetPort: targetPort);
        }).ToList();

        // App Service routes all traffic for a site to a single container port (WEBSITES_PORT),
        // so every endpoint must agree on the target port.
        var targetPorts = resolvedEndpoints.Select(r => r.TargetPort).Distinct().ToList();
        if (targetPorts.Count > 1)
        {
            throw new NotSupportedException(
                "Azure App Service supports a single target port per app. " +
                $"Resource '{ComputeResource.Name}' declares endpoints on ports: {string.Join(", ", targetPorts)}.");
        }

        _targetPort = targetPorts[0];

        foreach (var (endpoint, targetPort) in resolvedEndpoints)
        {
            // App Service terminates TLS on *.azurewebsites.net, so HTTP endpoints are upgraded to HTTPS
            // by default (mirrors Aspire's App Service integration). Opt out with WithHttpsUpgrade(false).
            var scheme = _environment.HttpsUpgrade && endpoint.UriScheme is "http" ? "https" : endpoint.UriScheme;
            var port = scheme is "https" ? 443 : 80;
            _endpointMapping[endpoint.Name] = new(scheme, port, targetPort, TlsEnabled: scheme is "https");
        }
    }

    /// <inheritdoc />
    protected override async Task<PulumiResource> BuildComputeResourceAsync()
    {
        var appName = NormalizedName;
        var image = await GetPushedContainerImageAsync().ConfigureAwait(false) ?? $"{appName}:latest";

        var appSettings = new List<NameValuePairArgs>();
        foreach (var (name, value) in EnvironmentVariables)
        {
            var resolved = await ResolveValueAsync(value).ConfigureAwait(false);
            appSettings.Add(new NameValuePairArgs { Name = name, Value = resolved.Value });
        }

        if (_targetPort is { } targetPort)
        {
            // App Service routes front-end traffic to the container port declared in WEBSITES_PORT.
            appSettings.Add(new NameValuePairArgs { Name = "WEBSITES_PORT", Value = targetPort.ToString() });
        }

        var siteConfig = new SiteConfigArgs
        {
            // Classic single-container Linux site pulling from the environment's registry. The image is
            // already fully qualified ({acrLoginServer}/{name}:{tag}) after the push step.
            LinuxFxVersion = $"DOCKER|{image}",
            AppSettings = appSettings,
        };

        if (_environment.ManagedIdentity is { } acrIdentity)
        {
            // Pull from ACR with the environment's user-assigned identity instead of registry credentials.
            // AcrUserManagedIdentityID expects the identity's *client id*, not its ARM resource id.
            siteConfig.AcrUseManagedIdentityCreds = true;
            siteConfig.AcrUserManagedIdentityID = acrIdentity.ClientId;
        }

        // App Service has no separate entrypoint/args split; combine them into the startup command.
        var commandParts = new List<object>();
        if (GetContainerEntrypoint() is { } entrypoint)
        {
            commandParts.Add(entrypoint);
        }

        commandParts.AddRange(Args);

        if (commandParts.Count > 0)
        {
            var partOutputs = new List<Output<string>>(commandParts.Count);
            foreach (var part in commandParts)
            {
                partOutputs.Add((await ResolveValueAsync(part).ConfigureAwait(false)).Value);
            }

            siteConfig.AppCommandLine = Output.All(partOutputs.ToArray()).Apply(parts => string.Join(' ', parts));
        }

        var webAppArgs = new WebAppArgs
        {
            // Physical site name carries the shared random suffix because {site}.azurewebsites.net is a
            // global DNS namespace. Composed lazily: the suffix is generated inside the Pulumi program.
            Name = _environmentContext.SiteSuffix.Apply(suffix => BuildSiteName(appName, suffix)),
            ResourceGroupName = _environment.ResourceGroup!.Name,
            Location = _environment.Location,
            ServerFarmId = _environment.AppServicePlan!.Id,
            Kind = "app,linux,container",
            HttpsOnly = _environment.HttpsUpgrade,
            SiteConfig = siteConfig,
        };

        if (_environment.ManagedIdentity is { } identity)
        {
            webAppArgs.Identity = new ManagedServiceIdentityArgs
            {
                Type = ManagedServiceIdentityType.UserAssigned,
                UserAssignedIdentities = new[] { identity.Id },
            };
        }

        var webApp = new WebApp(appName, webAppArgs);

        // Export the site hostname so the per-resource print-summary step can surface a URL.
        PublishingContext.AddOutput($"{appName}-hostname", webApp.DefaultHostName);

        return webApp;
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
                // The suffix is shared across the environment, so any context can compose a sibling's host.
                return GetEndpointValue(ownerContext.NormalizedName, mapping, property);
            }
        }

        return FallbackEndpointValue(endpoint, property);
    }

    /// <summary>
    /// Composes an endpoint property value from a mapping. All App Service endpoints resolve to the site's
    /// public <c>{site}.azurewebsites.net</c> hostname; there is no internal-only addressing like Container Apps.
    /// </summary>
    private Output<string> GetEndpointValue(string appName, EndpointMapping mapping, EndpointProperty property)
    {
        var (scheme, port, targetPort, tlsEnabled) = mapping;

        // The site suffix is generated inside the Pulumi program, so hosts must be composed lazily from
        // the Output rather than as a literal string.
        Output<string> GetHost(string prefix = "", string suffix = "")
        {
            return _environmentContext.SiteSuffix.Apply(s => $"{prefix}{BuildHost(appName, s)}{suffix}");
        }

        return property switch
        {
            // 80/443 are the default ports for their schemes, so URLs omit them.
            EndpointProperty.Url => GetHost($"{scheme}://"),
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
    /// Builds the globally unique App Service site name. This is a pure function so the naming formula can be
    /// unit-tested without a Pulumi engine.
    /// </summary>
    /// <param name="appName">The normalized resource name.</param>
    /// <param name="suffix">The environment's shared random suffix.</param>
    /// <remarks>
    /// Mirrors Aspire's <c>take(toLower("{name}-{uniqueString(...)}"), 60)</c>: site names are DNS labels
    /// limited to 60 characters.
    /// </remarks>
    internal static string BuildSiteName(string appName, string suffix)
    {
        var siteName = $"{appName}-{suffix}".ToLowerInvariant();
        return siteName.Length <= 60 ? siteName : siteName[..60];
    }

    /// <summary>Builds the public hostname for a site.</summary>
    /// <param name="appName">The normalized resource name.</param>
    /// <param name="suffix">The environment's shared random suffix.</param>
    internal static string BuildHost(string appName, string suffix) =>
        $"{BuildSiteName(appName, suffix)}.azurewebsites.net";

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
