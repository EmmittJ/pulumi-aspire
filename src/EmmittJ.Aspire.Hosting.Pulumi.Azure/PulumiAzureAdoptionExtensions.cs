// Licensed under the MIT License.

#pragma warning disable ASPIRECOMPUTE001 // compute-resource APIs are experimental

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
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
    /// Publish previews (<see cref="PulumiOperation.Preview"/>) substitute deterministic placeholders for
    /// ambient-credential invokes and unresolvable parameters (for example container images that have not
    /// been pushed yet), so the preview artifact is produced without Azure credentials. Deploys use real
    /// invokes and fail fast on unresolvable values.
    /// </remarks>
    public static Task<AzureTranslationContext> TranslateAzureEnvironmentAsync(
        this PulumiAdoptionContext context,
        AzureAdoptionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new AzureEnvironmentTranslation(context, options ?? new AzureAdoptionOptions()).RunAsync();
    }
}

/// <summary>
/// One run of the Azure adoption frontend: discovers the adopted environment's templates, orders them by
/// their <see cref="BicepOutputReference"/> parameter edges, and drives
/// <see cref="AzureProvisioningTemplateTranslator"/> over each.
/// </summary>
internal sealed class AzureEnvironmentTranslation(PulumiAdoptionContext context, AzureAdoptionOptions options)
{
    public async Task<AzureTranslationContext> RunAsync()
    {
        var usePlaceholders = context.Operation == PulumiOperation.Preview;

        var targets = new List<AzureBicepResource>();
        foreach (var (_, annotation) in context.GetDeploymentTargets())
        {
            if (annotation.DeploymentTarget is AzureBicepResource bicep)
            {
                targets.Add(bicep);
            }
        }

        var templates = CollectTemplates(targets);
        if (templates.Count == 0)
        {
            throw new InvalidOperationException(
                $"The adopted environment '{context.AdoptedEnvironment.Name}' produced no Azure provisioning " +
                "model to translate: no Bicep-backed deployment targets are attached and the environment " +
                "itself is not an Azure resource. TranslateAzureEnvironmentAsync only supports native Azure " +
                "environments (for example AddAzureContainerAppEnvironment); make sure the native prepare " +
                "steps ran before the Pulumi program (the backend's deploy step depends on 'before-start' " +
                "for exactly this reason).");
        }

        var (resourceGroupName, location) = CreateResourceGroup(usePlaceholders);

        var translationContext = new AzureTranslationContext(
            resourceGroupName,
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
        context.AddOutput("resourceGroupName", resourceGroupName);
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
    /// Collects every template to translate — the adopted environment's own provisioning resource plus the
    /// Bicep-backed deployment targets — expanded to the transitive closure of the templates their
    /// parameters reference through <see cref="BicepOutputReference"/>s, in dependency order (referenced
    /// templates first). Ordering is deterministic: seeds are visited environment-first, then targets by name.
    /// </summary>
    private List<AzureBicepResource> CollectTemplates(List<AzureBicepResource> targets)
    {
        var seeds = new List<AzureBicepResource>();
        if (context.AdoptedEnvironment is AzureBicepResource environmentBicep)
        {
            seeds.Add(environmentBicep);
        }

        seeds.AddRange(targets.OrderBy(static t => t.Name, StringComparer.Ordinal));

        var ordered = new List<AzureBicepResource>();
        var visited = new HashSet<AzureBicepResource>();

        void Visit(AzureBicepResource resource)
        {
            if (!visited.Add(resource))
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
    private (Output<string> Name, Output<string> Location) CreateResourceGroup(bool usePlaceholders)
    {
        var name = options.ResourceGroupName ?? $"{context.AdoptedEnvironment.Name}-rg";
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
