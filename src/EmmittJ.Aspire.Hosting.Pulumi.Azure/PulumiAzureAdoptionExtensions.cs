// Licensed under the MIT License.

#pragma warning disable ASPIRECOMPUTE001 // compute-resource APIs are experimental
#pragma warning disable ASPIREPIPELINES001 // Pipeline APIs are experimental
#pragma warning disable ASPIRECOMPUTE003  // IContainerRegistry is experimental

using System.ComponentModel;
using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Pipelines;
using EmmittJ.Aspire.Hosting.Pulumi.Azure;
using Microsoft.Extensions.Logging;
using Pulumi;
using Pulumi.AzureNative.Resources;

namespace EmmittJ.Aspire.Hosting.Pulumi;

/// <summary>
/// The Azure frontend for the adopt-and-traverse mode: translates the provisioning model an adopted native
/// Azure compute environment materializes (the environment's <see cref="AzureProvisioningResource"/>s and
/// each compute resource's Bicep-backed deployment target) into Pulumi azure-native resources inside the
/// current Pulumi program.
/// </summary>
public static class PulumiAzureAdoptionExtensions
{
    /// <summary>
    /// Translates the adopted Azure environment's provisioning model into Pulumi azure-native resources:
    /// creates (or references) the target resource group, translates the environment templates and every
    /// Bicep-backed deployment target in dependency order, and exports each template's outputs as stack
    /// outputs (<c>{template}_{output}</c>, plus <c>resourceGroupName</c>).
    /// </summary>
    /// <param name="context">The adoption context passed to the <c>PublishAsPulumi</c> program.</param>
    /// <param name="options">Optional translation settings (resource group, location, customization hook).</param>
    /// <returns>
    /// The translation context, exposing the translated templates and their live outputs for post-processing.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Publish previews (<see cref="PulumiOperation.Preview"/>) substitute deterministic placeholders for
    /// ambient-credential invokes and unresolvable parameters (for example container images that have not
    /// been pushed yet), so the preview artifact is produced without Azure credentials. Deploys use real
    /// invokes and fail fast on unresolvable values.
    /// </para>
    /// <para>
    /// When the environment has a registry-first phase (<see cref="PulumiEnvironmentResource.RegistryPhase"/>), the
    /// container registry templates that phase owns are excluded here so the same ARM resources are not
    /// managed by two stacks. Their outputs still feed deployment-target parameters: the registry phase's
    /// <c>up</c> back-propagates the deployed values into the registry resource's outputs, which the
    /// excluded templates' <c>BicepOutputReference</c> parameters resolve from.
    /// </para>
    /// </remarks>
    public static Task<AzureTranslationContext> TranslateAzureEnvironmentAsync(
        this PulumiPublishingContext context,
        AzureAdoptionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new AzureEnvironmentTranslation(context, options ?? new AzureAdoptionOptions()).RunAsync();
    }

    /// <summary>
    /// Translates only the adopted Azure environment's container registry templates into Pulumi
    /// azure-native resources — the program body for the registry-first phase
    /// (<see cref="PulumiEnvironmentResource.RegistryPhase"/>), which provisions the registry into its own
    /// stack before Aspire's push step runs. Exports each template's outputs as stack outputs
    /// (<c>{template}_{output}</c>, plus <c>resourceGroupName</c>); exporting is load-bearing: it roots the
    /// applies that back-propagate the deployed values into the registry resource's outputs, which is what
    /// lets Aspire's push step and the registry login callback resolve the registry name and endpoint.
    /// </summary>
    /// <param name="context">The adoption context passed to the registry phase program.</param>
    /// <param name="options">
    /// Optional translation settings. The resource group defaults to
    /// <c>{adopted-environment-name}-registry-rg</c> — a group of its own, owned by the registry stack, so
    /// destroying either stack never deletes resources managed by the other.
    /// </param>
    /// <returns>
    /// The translation context, exposing the translated templates and their live outputs for post-processing.
    /// </returns>
    public static Task<AzureTranslationContext> TranslateAzureRegistriesAsync(
        this PulumiPublishingContext context,
        AzureAdoptionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new AzureEnvironmentTranslation(context, options ?? new AzureAdoptionOptions()).RunRegistriesAsync();
    }

    /// <summary>
    /// Creates the ready-made registry-first phase for adopted Azure environments: provisions the
    /// environment's container registry templates with <see cref="TranslateAzureRegistriesAsync"/> and
    /// authenticates Docker to the registry with <c>az acr login</c> (override
    /// <see cref="PulumiRegistryPhase.LoginCallback"/> for other credential flows). Pass the result to
    /// <c>PublishAsPulumi(..., registryPhase: ...)</c>.
    /// </summary>
    /// <param name="options">
    /// Optional translation settings for the registry stack (see
    /// <see cref="TranslateAzureRegistriesAsync"/> for the resource-group default).
    /// </param>
    public static PulumiRegistryPhase CreateAzureRegistryPhase(AzureAdoptionOptions? options = null) =>
        new(context => context.TranslateAzureRegistriesAsync(options))
        {
            LoginCallback = AzureCliAcrLoginAsync,
        };

    /// <summary>
    /// Authenticates Docker to an Azure Container Registry by running
    /// <c>az acr login --name {registryName}</c>. Requires the Azure CLI to be installed and logged in.
    /// </summary>
    private static async Task AzureCliAcrLoginAsync(PipelineStepContext context, IContainerRegistry registry)
    {
        var registryName = await registry.Name.GetValueAsync(context.CancellationToken).ConfigureAwait(false);

        if (string.IsNullOrEmpty(registryName))
        {
            throw new InvalidOperationException(
                "Registry name not available. Ensure the registry is provisioned before the login step runs.");
        }

        context.Logger.LogInformation("Logging in to Azure Container Registry '{RegistryName}'.", registryName);

        // On Windows the Azure CLI is a .cmd shim, which needs cmd.exe when UseShellExecute is false
        // (required for stdout/stderr redirection).
        var useShellWrapper = OperatingSystem.IsWindows();
        var startInfo = new ProcessStartInfo
        {
            FileName = useShellWrapper ? "cmd.exe" : "az",
            Arguments = useShellWrapper ? $"/c az acr login --name {registryName}" : $"acr login --name {registryName}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 2) // ERROR_FILE_NOT_FOUND
        {
            throw new InvalidOperationException(
                "Azure CLI ('az') is not installed or not found in PATH. Install it from https://aka.ms/installazurecli and run 'az login'.",
                ex);
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(context.CancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(context.CancellationToken);
        await process.WaitForExitAsync(context.CancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(output))
        {
            context.Logger.LogDebug("Azure CLI output: {Output}", output);
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Azure ACR login failed with exit code {process.ExitCode}. Error: {error}");
        }
    }
}

/// <summary>
/// One run of the Azure adoption frontend: discovers the adopted environment's templates, orders them by
/// their <see cref="BicepOutputReference"/> parameter edges, and drives
/// <see cref="AzureProvisioningTemplateTranslator"/> over each.
/// </summary>
internal sealed class AzureEnvironmentTranslation(PulumiPublishingContext context, AzureAdoptionOptions options)
{
    public async Task<AzureTranslationContext> RunAsync()
    {
        var targets = new List<AzureBicepResource>();
        foreach (var (_, annotation) in context.GetDeploymentTargets())
        {
            if (annotation.DeploymentTarget is AzureBicepResource bicep)
            {
                targets.Add(bicep);
            }
        }

        var seeds = new List<AzureBicepResource>();
        if (context.AdoptedEnvironment is AzureBicepResource environmentBicep)
        {
            seeds.Add(environmentBicep);
        }

        seeds.AddRange(targets.OrderBy(static t => t.Name, StringComparer.Ordinal));

        // Registry templates owned by the registry-first phase are excluded so the same ARM resources are
        // not managed by two stacks. Their BicepOutputReference parameters resolve through the value
        // resolver instead: the registry phase's up back-propagated the deployed values into the registry
        // resource's outputs (previews substitute placeholders).
        var excluded = context.Environment.RegistryPhase is null
            ? []
            : CollectRegistryTemplates();

        var templates = CollectTemplates(seeds, excluded);
        if (templates.Count == 0)
        {
            throw new InvalidOperationException(
                $"The adopted environment '{context.AdoptedEnvironment.Name}' produced no Azure provisioning " +
                "model to translate: no Bicep-backed deployment targets are attached and the environment " +
                "itself is not an Azure resource. TranslateAzureEnvironmentAsync only supports native Azure " +
                "environments (for example AddAzureContainerAppEnvironment); make sure the native prepare " +
                "steps ran before the Pulumi program (the environment's deploy step depends on 'before-start' " +
                "for exactly this reason).");
        }

        return await TranslateAsync(templates, options.ResourceGroupName ?? $"{context.AdoptedEnvironment.Name}-rg")
            .ConfigureAwait(false);
    }

    public async Task<AzureTranslationContext> RunRegistriesAsync()
    {
        var seeds = CollectRegistryTemplates()
            .OrderBy(static r => r.Name, StringComparer.Ordinal)
            .ToList();

        if (seeds.Count == 0)
        {
            throw new InvalidOperationException(
                $"The adopted environment '{context.AdoptedEnvironment.Name}' attached no Bicep-backed " +
                "container registry to its deployment targets, so there is nothing for the registry phase " +
                "to provision. TranslateAzureRegistriesAsync only supports native Azure environments that " +
                "provision their own registry (for example AddAzureContainerAppEnvironment); make sure the " +
                "native prepare steps ran before the Pulumi program (the registry step depends on " +
                "'before-start' for exactly this reason).");
        }

        var templates = CollectTemplates(seeds, excluded: []);

        // The registry stack owns its own resource group by default so destroying either stack never
        // deletes resources managed by the other.
        return await TranslateAsync(templates, options.ResourceGroupName ?? $"{context.AdoptedEnvironment.Name}-registry-rg")
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Collects the distinct Bicep-backed container registries the adopted environment attached to its
    /// deployment targets — the templates the registry-first phase owns. Native Azure environments (for
    /// example Azure Container Apps) attach <em>themselves</em> as the deployment target's registry and
    /// delegate the registry surface to their implicit Azure Container Registry resource, so compute
    /// environments are unwrapped to that resource — the registry phase must own only the registry
    /// template, never the whole environment.
    /// </summary>
    private HashSet<AzureBicepResource> CollectRegistryTemplates()
    {
        var templates = new HashSet<AzureBicepResource>();
        foreach (var registry in context.GetContainerRegistries())
        {
            var resolved = registry is IAzureComputeEnvironmentResource { ContainerRegistry: { } actual }
                ? actual
                : registry;
            if (resolved is AzureBicepResource bicep)
            {
                templates.Add(bicep);
            }
        }

        return templates;
    }

    private async Task<AzureTranslationContext> TranslateAsync(List<AzureBicepResource> templates, string resourceGroupName)
    {
        var usePlaceholders = context.Operation == PulumiOperation.Preview;
        var (groupName, location) = CreateResourceGroup(resourceGroupName, usePlaceholders);

        var translationContext = new AzureTranslationContext(
            groupName,
            location,
            new PulumiValueResolver(context.ExecutionContext, context.CancellationToken),
            context.Logger,
            usePlaceholders)
        {
            ConfigureResource = options.ConfigureResource,
        };

        foreach (var template in templates)
        {
            context.Logger.LogInformation(
                "Translating Azure template '{Template}' for the adopted environment '{Environment}'.",
                template.Name, context.AdoptedEnvironment.Name);
            await AzureProvisioningTemplateTranslator.TranslateAsync(translationContext, template).ConfigureAwait(false);
        }

        // Register every template output as a stack output. This is load-bearing beyond observability: the
        // translator back-propagates deployed values into AzureBicepResource.Outputs through applies that
        // only run because the outputs are rooted here.
        context.AddOutput("resourceGroupName", groupName);
        foreach (var translated in translationContext.Templates.Values)
        {
            foreach (var (name, value) in translated.Outputs)
            {
                context.AddOutput($"{translated.Name}_{name}", value);
            }
        }

        return translationContext;
    }

    /// <summary>
    /// Collects every template to translate — the seeds expanded to the transitive closure of the templates
    /// their parameters reference through <see cref="BicepOutputReference"/>s, in dependency order
    /// (referenced templates first). Excluded templates (and anything only they reference) are skipped.
    /// Ordering is deterministic: seeds are visited in the order given.
    /// </summary>
    private static List<AzureBicepResource> CollectTemplates(
        List<AzureBicepResource> seeds,
        HashSet<AzureBicepResource> excluded)
    {
        var ordered = new List<AzureBicepResource>();
        var visited = new HashSet<AzureBicepResource>();

        void Visit(AzureBicepResource resource)
        {
            if (excluded.Contains(resource) || !visited.Add(resource))
            {
                return;
            }

            foreach (var referenced in resource.Parameters.Values
                         .OfType<BicepOutputReference>()
                         .Select(static reference => reference.Resource)
                         .Distinct()
                         .OrderBy(static r => r.Name, StringComparer.Ordinal))
            {
                Visit(referenced);
            }

            ordered.Add(resource);
        }

        foreach (var seed in seeds)
        {
            Visit(seed);
        }

        return ordered;
    }

    /// <summary>
    /// Creates the target resource group (or references an existing one) and returns the name/location
    /// outputs the translation context threads through every translated resource. During deploys the
    /// created group's outputs are used so translated resources gain an implicit dependency on it; previews
    /// keep deterministic literals.
    /// </summary>
    private (Output<string> Name, Output<string> Location) CreateResourceGroup(string name, bool usePlaceholders)
    {
        var location = options.Location ?? new Config("azure-native").Get("location");

        if (options.UseExistingResourceGroup)
        {
            var existingLocation = location is not null
                ? Output.Create(location)
                : usePlaceholders
                    ? Output.Create("<preview:location>")
                    : GetResourceGroup.Invoke(new GetResourceGroupInvokeArgs { ResourceGroupName = name })
                        .Apply(static group => group.Location);
            return (Output.Create(name), existingLocation);
        }

        if (location is null && !usePlaceholders)
        {
            throw new InvalidOperationException(
                $"An Azure location is required to create resource group '{name}'. Set " +
                $"{nameof(AzureAdoptionOptions)}.{nameof(AzureAdoptionOptions.Location)} or the " +
                "'azure-native:location' Pulumi config value.");
        }

        var resourceGroup = new ResourceGroup(name, new ResourceGroupArgs
        {
            ResourceGroupName = name,
            Location = location ?? "<preview:location>",
        });

        return usePlaceholders
            ? (Output.Create(name), Output.Create(location ?? "<preview:location>"))
            : (resourceGroup.Name, resourceGroup.Location);
    }
}
