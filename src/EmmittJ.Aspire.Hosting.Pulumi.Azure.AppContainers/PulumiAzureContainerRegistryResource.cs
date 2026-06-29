// Licensed under the MIT License.

#pragma warning disable ASPIREPIPELINES001 // Pipeline APIs are experimental

using Aspire.Hosting.Pipelines;
using EmmittJ.Aspire.Hosting.Pulumi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pulumi;
using Pulumi.Automation;
using Pulumi.AzureNative.ContainerRegistry;
using Pulumi.AzureNative.Resources;
using Pulumi.Random;
using Task = System.Threading.Tasks.Task;

namespace EmmittJ.Aspire.Hosting.Pulumi.Azure.AppContainers;

/// <summary>
/// An Azure Container Registry provisioned through Pulumi as its own stack before the environment is deployed.
/// </summary>
public sealed class PulumiAzureContainerRegistryResource : PulumiContainerRegistryResource
{
    private string? _resolvedResourceGroupName;

    /// <summary>
    /// Initializes a new instance of the <see cref="PulumiAzureContainerRegistryResource"/> class.
    /// </summary>
    /// <param name="name">The registry resource name.</param>
    /// <param name="pulumiProjectName">The Pulumi project name that groups stacks.</param>
    /// <param name="stackName">The Pulumi stack name used to provision the registry.</param>
    /// <param name="location">The Azure region for the registry.</param>
    public PulumiAzureContainerRegistryResource(string name, string pulumiProjectName, string stackName, string location)
        : base(name)
    {
        PulumiProjectName = pulumiProjectName;
        StackName = stackName;
        Location = location;

        // Default to Azure CLI authentication; users can override LoginCallback.
        LoginCallback = PulumiContainerRegistryHelpers.CreateAzureCliLoginCallback();
    }

    /// <inheritdoc />
    public override string PulumiProjectName { get; }

    /// <inheritdoc />
    public override string StackName { get; }

    /// <summary>Gets or sets the Azure region for the container registry.</summary>
    public string Location { get; set; }

    /// <summary>Gets or sets the resource group name. Defaults to <c>{StackName}-rg</c>.</summary>
    public string? ResourceGroupName { get; set; }

    /// <summary>Gets or sets the container registry SKU.</summary>
    public SkuName SkuName { get; set; } = SkuName.Basic;

    /// <summary>Gets or sets whether the admin user is enabled on the registry.</summary>
    public bool EnableAdminUser { get; set; } = true;

    /// <summary>Gets the resolved resource group name after provisioning.</summary>
    public string? ResolvedResourceGroupName => _resolvedResourceGroupName;

    /// <inheritdoc />
    protected override async Task<(string Name, string Endpoint)> CreateRegistryAsync(PipelineStepContext context)
    {
        var runner = context.Services.GetRequiredService<PulumiRunner>();
        var logger = context.Services.GetRequiredService<ILoggerFactory>().CreateLogger<PulumiAzureContainerRegistryResource>();

        logger.LogInformation("Provisioning Azure Container Registry stack '{StackName}'...", StackName);

        var result = await runner.ForStack(PulumiProjectName, StackName)
            .WithWorkDir(WorkingDirectory)
            .WithConfiguration(ConfigureRegistryStackAsync)
            .UpAsync(CreateRegistryProgram, context.CancellationToken)
            .ConfigureAwait(false);

        if (!result.Outputs.TryGetValue("registryName", out var registryNameOutput) ||
            !result.Outputs.TryGetValue("loginServer", out var loginServerOutput))
        {
            throw new InvalidOperationException(
                "The registry stack did not export the expected 'registryName' and 'loginServer' outputs.");
        }

        var resolvedName = registryNameOutput.Value?.ToString()
            ?? throw new InvalidOperationException("Registry name output was null.");
        var resolvedEndpoint = loginServerOutput.Value?.ToString()
            ?? throw new InvalidOperationException("Login server output was null.");

        if (result.Outputs.TryGetValue("resourceGroupName", out var resourceGroupOutput))
        {
            _resolvedResourceGroupName = resourceGroupOutput.Value?.ToString();
        }

        return (resolvedName, resolvedEndpoint);
    }

    /// <inheritdoc />
    protected override async Task DestroyRegistryAsync(PipelineStepContext context)
    {
        var runner = context.Services.GetRequiredService<PulumiRunner>();
        var logger = context.Services.GetRequiredService<ILoggerFactory>().CreateLogger<PulumiAzureContainerRegistryResource>();

        logger.LogInformation("Destroying Azure Container Registry stack '{StackName}'...", StackName);

        await runner.ForStack(PulumiProjectName, StackName)
            .WithWorkDir(WorkingDirectory)
            .WithConfiguration(ConfigureRegistryStackAsync)
            .DestroyAsync(CreateRegistryProgram, context.CancellationToken)
            .ConfigureAwait(false);
    }

    private Task<IDictionary<string, object?>> CreateRegistryProgram()
    {
        var resourceGroupName = ResourceGroupName ?? $"{StackName}-rg";
        var resourceGroup = new ResourceGroup(resourceGroupName, new ResourceGroupArgs
        {
            ResourceGroupName = resourceGroupName,
            Location = Location,
        });

        // Deterministic suffix keyed on the stack name so re-running keeps the same generated registry name.
        // ACR names must be globally unique, 5-50 alphanumeric chars: "acr" + 13 lowercase chars = 16 total.
        var suffix = new RandomString($"{StackName}-acr-suffix", new RandomStringArgs
        {
            Length = 13,
            Special = false,
            Upper = false,
            Keepers = new InputMap<string> { ["stack"] = StackName },
        });

        var registry = new Registry($"{Name}-acr", new RegistryArgs
        {
            RegistryName = suffix.Result.Apply(s => $"acr{s}"),
            ResourceGroupName = resourceGroup.Name,
            Location = Location,
            Sku = new global::Pulumi.AzureNative.ContainerRegistry.Inputs.SkuArgs { Name = SkuName },
            AdminUserEnabled = EnableAdminUser,
        });

        IDictionary<string, object?> outputs = new Dictionary<string, object?>
        {
            ["registryName"] = registry.Name,
            ["loginServer"] = registry.LoginServer,
            ["resourceGroupName"] = resourceGroup.Name,
        };

        return Task.FromResult(outputs);
    }

    private async Task ConfigureRegistryStackAsync(WorkspaceStack stack, CancellationToken cancellationToken)
    {
        await stack.SetConfigAsync("azure-native:location", new ConfigValue(Location), cancellationToken).ConfigureAwait(false);
    }
}
