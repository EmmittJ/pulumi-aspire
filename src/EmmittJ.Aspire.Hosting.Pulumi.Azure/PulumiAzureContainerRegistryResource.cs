// Licensed under the Apache License, Version 2.0.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIRECOMPUTE003

using System.Collections.Immutable;
using Aspire.Hosting.Pipelines;
using EmmittJ.Aspire.Hosting.Pulumi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pulumi;
using Pulumi.Automation;
using Pulumi.AzureNative.ContainerRegistry;
using Pulumi.AzureNative.Resources;
using Pulumi.Random;

namespace EmmittJ.Aspire.Hosting.Pulumi.Azure;

/// <summary>
/// Azure Container Registry resource provisioned using Pulumi.
/// </summary>
/// <remarks>
/// <para>
/// This resource provisions an Azure Container Registry as a separate Pulumi stack.
/// It is deployed before the main environment stack, allowing images to be pushed
/// before deploying the compute resources.
/// </para>
/// <para>
/// By default, it uses Azure CLI authentication for Docker login after provisioning.
/// </para>
/// <para>
/// <strong>Naming:</strong> The registry uses <see cref="ResourcePrefix"/> for stack naming.
/// The stack name is <c>{ResourcePrefix}-registry</c>. The <see cref="PulumiProjectName"/> groups
/// related stacks in the Pulumi console and defaults to <see cref="ResourcePrefix"/>.
/// </para>
/// </remarks>
public sealed class PulumiAzureContainerRegistryResource : PulumiContainerRegistryResource
{
    private WorkspaceStack? _stack;
    private IImmutableDictionary<string, OutputValue>? _outputs;
    private string? _resolvedResourceGroupName;

    /// <summary>
    /// Initializes a new instance of the <see cref="PulumiAzureContainerRegistryResource"/> class.
    /// </summary>
    /// <param name="name">The name of the container registry resource (used as Aspire resource name).</param>
    /// <param name="environmentName">The environment name (e.g., "dev", "staging"). If null, uses <paramref name="name"/>.</param>
    /// <param name="projectName">The Pulumi project name for grouping stacks. If null, uses <paramref name="environmentName"/>.</param>
    /// <param name="location">The Azure region for the container registry. Defaults to "eastus".</param>
    public PulumiAzureContainerRegistryResource(
        string name,
        string? environmentName = null,
        string? projectName = null,
        string? location = null)
        : base(name)
    {
        // EnvironmentName is the logical environment (e.g., "dev", "staging")
        EnvironmentName = environmentName ?? name;
        
        // PulumiProjectName groups stacks in Pulumi console
        PulumiProjectName = projectName ?? EnvironmentName;
        
        Location = location ?? "eastus";

        // Default to Azure CLI login
        LoginCallback = PulumiContainerRegistryHelpers.CreateAzureCliLoginCallback();
    }

    /// <summary>
    /// Gets or sets the Pulumi project name.
    /// All stacks are grouped under this project in the Pulumi console.
    /// </summary>
    public override string PulumiProjectName { get; set; }

    /// <summary>
    /// Gets the environment name (e.g., "dev", "staging", "prod").
    /// </summary>
    /// <remarks>
    /// Combined with <see cref="PulumiProjectName"/> to form the <see cref="ResourcePrefix"/>.
    /// </remarks>
    public override string EnvironmentName { get; }

    /// <summary>
    /// Gets or sets the Azure region for the container registry.
    /// </summary>
    public string Location { get; set; } = "eastus";

    /// <summary>
    /// Gets or sets the resource group name.
    /// If not set, one will be created with the pattern "{ResourcePrefix}-registry-rg".
    /// This should match the environment's resource group for shared deployments.
    /// </summary>
    public string? ResourceGroupName { get; set; }

    /// <summary>
    /// Gets or sets the Pulumi organization name.
    /// </summary>
    public string? Organization { get; set; }

    /// <summary>
    /// Gets or sets the container registry SKU.
    /// </summary>
    public SkuName SkuName { get; set; } = SkuName.Basic;

    /// <summary>
    /// Gets or sets whether to enable admin user access.
    /// </summary>
    public bool EnableAdminUser { get; set; } = true;

    /// <summary>
    /// Gets the resolved resource group name after provisioning.
    /// </summary>
    public string? ResolvedResourceGroupName => _resolvedResourceGroupName;

    /// <summary>
    /// Gets the Pulumi stack after provisioning.
    /// </summary>
    internal WorkspaceStack? Stack => _stack;

    /// <summary>
    /// Gets the Pulumi outputs after provisioning.
    /// </summary>
    internal IImmutableDictionary<string, OutputValue>? Outputs => _outputs;

    /// <inheritdoc />
    protected override async Task<(string Name, string Endpoint)> CreateRegistryAsync(PipelineStepContext context)
    {
        var logger = context.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger<PulumiAzureContainerRegistryResource>();

        // Use the configured project and stack names
        var projectName = PulumiProjectName;
        var stackName = $"{ResourcePrefix}-registry";

        logger.LogInformation(
            "Creating Azure Container Registry via Pulumi project '{ProjectName}' stack '{StackName}'...",
            projectName, stackName);

        // Create Pulumi stack using automation API
        var stackArgs = new InlineProgramArgs(projectName, stackName, PulumiFn.Create(() =>
        {
            // Use ResourcePrefix for unique resource group naming
            var rgName = ResourceGroupName ?? $"{ResourcePrefix}-registry-rg";
            var resourceGroup = new ResourceGroup(rgName, new ResourceGroupArgs
            {
                ResourceGroupName = rgName,
                Location = Location
            });

            // Create a deterministic random suffix using ResourcePrefix as keeper
            // This ensures the same suffix is generated for the same resource prefix
            var suffix = new RandomString($"{ResourcePrefix}-acr-suffix", new RandomStringArgs
            {
                Length = 13,
                Special = false,
                Upper = false,
                Keepers = new InputMap<string>
                {
                    ["prefix"] = ResourcePrefix
                }
            });

            // ACR names: "acr" + 13-char random suffix = 16 chars total
            // Example: acrh8k2m9p4q1w3z
            var registryName = suffix.Result.Apply(s => $"acr{s}");

            // Create the container registry
            var registry = new Registry($"{Name}-acr", new RegistryArgs
            {
                RegistryName = registryName,
                ResourceGroupName = resourceGroup.Name,
                Location = Location,
                Sku = new global::Pulumi.AzureNative.ContainerRegistry.Inputs.SkuArgs
                {
                    Name = SkuName
                },
                AdminUserEnabled = EnableAdminUser
            });

            // Export outputs
            return new Dictionary<string, object?>
            {
                ["registryName"] = registry.Name,
                ["loginServer"] = registry.LoginServer,
                ["resourceGroupName"] = resourceGroup.Name
            };
        }));

        // Configure workspace with execution directory
        var workDir = Path.Combine(Path.GetTempPath(), "pulumi-aspire", projectName);
        Directory.CreateDirectory(workDir);

        stackArgs.WorkDir = workDir;

        _stack = await LocalWorkspace.CreateOrSelectStackAsync(stackArgs, context.CancellationToken);

        // Configure Azure provider
        await _stack.SetConfigAsync("azure-native:location", new ConfigValue(Location), context.CancellationToken);

        logger.LogInformation("Running Pulumi up for registry stack '{StackName}'...", stackName);

        // Run Pulumi up
        var upResult = await _stack.UpAsync(new UpOptions
        {
            OnStandardOutput = msg => logger.LogDebug("[Pulumi] {Message}", msg),
            OnStandardError = msg => logger.LogWarning("[Pulumi] {Message}", msg)
        }, context.CancellationToken);

        _outputs = upResult.Outputs;

        if (!_outputs.TryGetValue("registryName", out var registryNameOutput) ||
            !_outputs.TryGetValue("loginServer", out var loginServerOutput))
        {
            throw new InvalidOperationException(
                "Failed to get registry outputs from Pulumi stack. " +
                "Expected 'registryName' and 'loginServer' outputs.");
        }

        var resolvedName = registryNameOutput.Value?.ToString()
            ?? throw new InvalidOperationException("Registry name output is null.");
        var resolvedEndpoint = loginServerOutput.Value?.ToString()
            ?? throw new InvalidOperationException("Login server output is null.");

        // Store the resolved resource group name
        if (_outputs.TryGetValue("resourceGroupName", out var resourceGroupOutput))
        {
            _resolvedResourceGroupName = resourceGroupOutput.Value?.ToString();
        }

        // Store the resolved values for IContainerRegistry
        SetResolvedValues(resolvedName, resolvedEndpoint);

        logger.LogInformation(
            "Azure Container Registry '{RegistryName}' created at '{Endpoint}'",
            resolvedName, resolvedEndpoint);

        return (resolvedName, resolvedEndpoint);
    }
}
