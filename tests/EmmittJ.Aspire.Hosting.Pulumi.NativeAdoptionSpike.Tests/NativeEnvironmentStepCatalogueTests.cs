// Licensed under the MIT License.

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EmmittJ.Aspire.Hosting.Pulumi.NativeAdoptionSpike.Tests;

/// <summary>
/// Catalogues the pipeline steps each native Aspire compute environment registers. These tests pin the step
/// names, tags, and graph edges the adoption seam depends on; if an Aspire version bump changes any of them,
/// these tests fail with the exact diff (see docs/spikes/native-environment-step-adoption.md).
/// </summary>
public class NativeEnvironmentStepCatalogueTests
{
    [Fact]
    public async Task AzureContainerAppEnvironment_RegistersExpectedSteps()
    {
        var builder = PipelineSpikeHarness.CreatePublishBuilder(out _);
        builder.AddAzureContainerAppEnvironment("aca-env");
        builder.AddContainer("web", "nginx:latest").WithHttpEndpoint(targetPort: 80);

        using var app = builder.Build();

        // Adding an ACA environment adds two supporting resources: the implicit AzureEnvironmentResource
        // (owns login/provisioning-context/aggregate-provision/destroy) and the ACR registry resource.
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var azureEnvironment = model.Resources.Single(r => r.GetType().Name == "AzureEnvironmentResource");
        var registry = model.Resources.Single(r => r.GetType().Name == "AzureContainerRegistryResource");
        var environment = model.Resources.Single(r => r.Name == "aca-env");

        var environmentSteps = await PipelineSpikeHarness.ResolveStepsAsync(app, azureEnvironment);
        Assert.Contains(environmentSteps, s => s.Name == "azure-prepare-resources" && s.RequiredBySteps.Contains(WellKnownPipelineSteps.BeforeStart));
        Assert.Contains(environmentSteps, s => s.Name == "validate-azure-login" && s.RequiredBySteps.Contains(WellKnownPipelineSteps.Deploy));
        Assert.Contains(environmentSteps, s => s.Name == "create-provisioning-context" && s.DependsOnSteps.Contains("validate-azure-login"));
        Assert.Contains(environmentSteps, s => s.Name == "provision-azure-bicep-resources" && s.Tags.Contains("provision-infra"));
        Assert.Contains(environmentSteps, s => s.Name == $"destroy-azure-{azureEnvironment.Name}" && s.RequiredBySteps.Contains(WellKnownPipelineSteps.Destroy));
        Assert.Contains(environmentSteps, s => s.Name == $"publish-{azureEnvironment.Name}" && s.RequiredBySteps.Contains(WellKnownPipelineSteps.Publish));

        var registrySteps = await PipelineSpikeHarness.ResolveStepsAsync(app, registry);
        Assert.Contains(registrySteps, s => s.Name == $"provision-{registry.Name}" && s.Tags.Contains("provision-infra") && s.RequiredBySteps.Contains("provision-azure-bicep-resources"));
        Assert.Contains(registrySteps, s => s.Name == $"login-to-acr-{registry.Name}" && s.Tags.Contains("acr-login") && s.RequiredBySteps.Contains(WellKnownPipelineSteps.PushPrereq));

        var acaSteps = await PipelineSpikeHarness.ResolveStepsAsync(app, environment);
        Assert.Contains(acaSteps, s => s.Name == "provision-aca-env" && s.Tags.Contains("provision-infra"));
        // The prepare step is the one that materializes DeploymentTargetAnnotations; it must stay untouched.
        Assert.Contains(acaSteps, s => s.Name == "prepare-azure-container-apps-aca-env" && s.RequiredBySteps.Contains(WellKnownPipelineSteps.BeforeStart));
        Assert.Contains(acaSteps, s => s.Name == "print-dashboard-url-aca-env" && s.Tags.Contains("print-summary"));
    }

    [Fact]
    public async Task AzureAppServiceEnvironment_RegistersExpectedSteps()
    {
        var builder = PipelineSpikeHarness.CreatePublishBuilder(out _);
        builder.AddAzureAppServiceEnvironment("appsvc-env");
        builder.AddContainer("web", "nginx:latest").WithHttpEndpoint(targetPort: 80);

        using var app = builder.Build();

        // Adding an App Service environment adds the same two supporting resources as ACA: the implicit
        // AzureEnvironmentResource (owns login/provisioning-context/aggregate-provision/destroy) and the
        // ACR registry resource. The execution-step surface is identical to ACA today; this catalogue pins
        // that equivalence so an Aspire version bump that diverges the two fails here with the exact diff.
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var azureEnvironment = model.Resources.Single(r => r.GetType().Name == "AzureEnvironmentResource");
        var registry = model.Resources.Single(r => r.GetType().Name == "AzureContainerRegistryResource");
        var environment = model.Resources.Single(r => r.Name == "appsvc-env");

        var environmentSteps = await PipelineSpikeHarness.ResolveStepsAsync(app, azureEnvironment);
        Assert.Contains(environmentSteps, s => s.Name == "azure-prepare-resources" && s.RequiredBySteps.Contains(WellKnownPipelineSteps.BeforeStart));
        Assert.Contains(environmentSteps, s => s.Name == "validate-azure-login" && s.RequiredBySteps.Contains(WellKnownPipelineSteps.Deploy));
        Assert.Contains(environmentSteps, s => s.Name == "create-provisioning-context" && s.DependsOnSteps.Contains("validate-azure-login"));
        Assert.Contains(environmentSteps, s => s.Name == "provision-azure-bicep-resources" && s.Tags.Contains("provision-infra"));
        Assert.Contains(environmentSteps, s => s.Name == $"destroy-azure-{azureEnvironment.Name}" && s.RequiredBySteps.Contains(WellKnownPipelineSteps.Destroy));
        Assert.Contains(environmentSteps, s => s.Name == $"publish-{azureEnvironment.Name}" && s.RequiredBySteps.Contains(WellKnownPipelineSteps.Publish));

        var registrySteps = await PipelineSpikeHarness.ResolveStepsAsync(app, registry);
        Assert.Contains(registrySteps, s => s.Name == $"provision-{registry.Name}" && s.Tags.Contains("provision-infra") && s.RequiredBySteps.Contains("provision-azure-bicep-resources"));
        Assert.Contains(registrySteps, s => s.Name == $"login-to-acr-{registry.Name}" && s.Tags.Contains("acr-login") && s.RequiredBySteps.Contains(WellKnownPipelineSteps.PushPrereq));

        var appServiceSteps = await PipelineSpikeHarness.ResolveStepsAsync(app, environment);
        Assert.Contains(appServiceSteps, s => s.Name == "provision-appsvc-env" && s.Tags.Contains("provision-infra"));
        // The prepare step is the one that materializes DeploymentTargetAnnotations; it must stay untouched.
        Assert.Contains(appServiceSteps, s => s.Name == "prepare-azure-app-service-appsvc-env" && s.RequiredBySteps.Contains(WellKnownPipelineSteps.BeforeStart));
        // App Service adds a publish-slot validation modeling step ACA does not have; it must keep running.
        Assert.Contains(appServiceSteps, s => s.Name == "validate-appservice-config-appsvc-env" && s.RequiredBySteps.Contains(WellKnownPipelineSteps.Publish));
        Assert.Contains(appServiceSteps, s => s.Name == "print-dashboard-url-appsvc-env" && s.Tags.Contains("print-summary"));

        // The AzureAppService selector must classify exactly the execution steps and never the modeling steps.
        var allSteps = environmentSteps.Concat(registrySteps).Concat(appServiceSteps).ToList();
        var suppressed = allSteps.Where(PulumiStepSuppressionSelector.AzureAppService.Matches).Select(s => s.Name).Order().ToArray();
        Assert.Equal(
            [
                "create-provisioning-context",
                $"destroy-azure-{azureEnvironment.Name}",
                $"login-to-acr-{registry.Name}",
                "provision-appsvc-env",
                $"provision-{registry.Name}",
                "provision-azure-bicep-resources",
                "validate-azure-login",
            ],
            suppressed);
    }

    [Fact]
    public async Task StructuralClassification_ReproducesPinnedSuppressionSet_AzureContainerApps()
    {
        var builder = PipelineSpikeHarness.CreatePublishBuilder(out _);
        builder.AddAzureContainerAppEnvironment("aca-env");
        builder.AddContainer("web", "nginx:latest").WithHttpEndpoint(targetPort: 80);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var azureEnvironment = model.Resources.Single(r => r.GetType().Name == "AzureEnvironmentResource");
        var registry = model.Resources.Single(r => r.GetType().Name == "AzureContainerRegistryResource");

        string[] expected =
        [
            "create-provisioning-context",
            $"destroy-azure-{azureEnvironment.Name}",
            $"login-to-acr-{registry.Name}",
            "provision-aca-env",
            $"provision-{registry.Name}",
            "provision-azure-bicep-resources",
            "validate-azure-login",
        ];

        await AssertStructuralAndDataSuppressionSetsMatch(app, PulumiStepSuppressionSelector.AzureContainerApps, expected);
    }

    [Fact]
    public async Task StructuralClassification_ReproducesPinnedSuppressionSet_AzureAppService()
    {
        var builder = PipelineSpikeHarness.CreatePublishBuilder(out _);
        builder.AddAzureAppServiceEnvironment("appsvc-env");
        builder.AddContainer("web", "nginx:latest").WithHttpEndpoint(targetPort: 80);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var azureEnvironment = model.Resources.Single(r => r.GetType().Name == "AzureEnvironmentResource");
        var registry = model.Resources.Single(r => r.GetType().Name == "AzureContainerRegistryResource");

        string[] expected =
        [
            "create-provisioning-context",
            $"destroy-azure-{azureEnvironment.Name}",
            $"login-to-acr-{registry.Name}",
            "provision-appsvc-env",
            $"provision-{registry.Name}",
            "provision-azure-bicep-resources",
            "validate-azure-login",
        ];

        await AssertStructuralAndDataSuppressionSetsMatch(app, PulumiStepSuppressionSelector.AzureAppService, expected);
    }

    /// <summary>
    /// The stability invariant behind structural classification: on the real pipeline graphs, the purely
    /// structural selector (public well-known steps/tags/interfaces only, zero provider strings) must
    /// classify exactly the same execution steps as the legacy pinned name/tag data — and exactly the
    /// pinned expected set. If an Aspire version bump moves either signal, this fails with the exact diff.
    /// </summary>
    private static async Task AssertStructuralAndDataSuppressionSetsMatch(
        DistributedApplication app,
        PulumiStepSuppressionSelector pinnedSelector,
        string[] expected)
    {
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var dataOnlySelector = pinnedSelector with { UseStructuralClassification = false };

        var structural = new List<string>();
        var dataOnly = new List<string>();
        foreach (var resource in model.Resources)
        {
            foreach (var step in await PipelineSpikeHarness.ResolveStepsAsync(app, resource))
            {
                if (PulumiStepSuppressionSelector.Structural.Matches(step, resource))
                {
                    structural.Add(step.Name);
                }

                if (dataOnlySelector.Matches(step, resource))
                {
                    dataOnly.Add(step.Name);
                }
            }
        }

        Assert.Equal(expected, structural.Order().ToArray());
        Assert.Equal(expected, dataOnly.Order().ToArray());
    }
}
