// Licensed under the MIT License.

using System.Collections.Concurrent;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Pipelines;
using Xunit;

namespace EmmittJ.Aspire.Hosting.Pulumi.NativeAdoptionSpike.Tests;

/// <summary>
/// End-to-end proof of the adoption seam: the native Azure Container Apps environment's execution steps are
/// suppressed, a stub "Pulumi backend" step is spliced into the deploy slot, and the full pipeline executes
/// in-process. Success = the stub observes the fully materialized provisioning model (environment Bicep, ACR
/// Bicep, and the per-compute-resource deployment-target Bicep) while no native execution step ran for real.
/// </summary>
public class AzureContainerAppAdoptionTests
{
    private static readonly string[] s_azureExecutionStepNames = ["validate-azure-login", "create-provisioning-context"];

    private static bool IsAzureExecutionStep(PipelineStep step) =>
        step.Tags.Contains("provision-infra")
        || step.Tags.Contains("acr-login")
        || step.Name.StartsWith("destroy-azure-", StringComparison.Ordinal)
        || s_azureExecutionStepNames.Contains(step.Name);

    private static bool IsSandboxOnlyStep(PipelineStep step) =>
        // Not part of the adoption seam: neutralized only so the sandbox run needs no docker build/push
        // or Azure CLI. In a real deployment these steps keep running (build/push stays with Aspire).
        step.Tags.Contains(WellKnownPipelineTags.BuildCompute)
        || step.Tags.Contains(WellKnownPipelineTags.PushContainerImage)
        || step.Name.StartsWith("publish-azure", StringComparison.Ordinal)
        || step.Name.StartsWith("print-dashboard-url-", StringComparison.Ordinal);

    [Fact]
    public async Task SuppressedNativeDeploy_StubStepWalksProvisioningModel()
    {
        var builder = PipelineSpikeHarness.CreatePublishBuilder(out var outputPath);
        builder.AddAzureContainerAppEnvironment("aca-env");
        builder.AddContainer("web", "nginx:latest").WithHttpEndpoint(targetPort: 80);

        var log = PipelineSpikeHarness.WrapNativeSteps(
            builder,
            resource => resource is AzureBicepResource || resource.GetType().Name == "AzureEnvironmentResource",
            step => IsAzureExecutionStep(step) || IsSandboxOnlyStep(step));

        var walked = new ConcurrentQueue<string>();
        builder.Pipeline.AddStep(
            "pulumi-stub-deploy",
            async context =>
            {
                foreach (var resource in context.Model.Resources)
                {
                    if (resource is AzureBicepResource bicep)
                    {
                        var template = await File.ReadAllTextAsync(bicep.GetBicepTemplateFile().Path, context.CancellationToken);
                        Assert.NotEmpty(template);
                        walked.Enqueue($"bicep:{resource.Name}");
                    }

                    if (resource.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var annotation))
                    {
                        walked.Enqueue($"target:{resource.Name}:{annotation.DeploymentTarget.Name}");
                        if (annotation.DeploymentTarget is AzureBicepResource targetBicep)
                        {
                            var template = await File.ReadAllTextAsync(targetBicep.GetBicepTemplateFile().Path, context.CancellationToken);
                            Assert.Contains("containerApp", template);
                            walked.Enqueue($"target-bicep:{annotation.DeploymentTarget.Name}");
                        }
                    }
                }
            },
            // Push ordering mirrors the real backend (deploy after images are pushed). The before-start
            // dependency is a spike finding: without it the scheduler may start this step while the native
            // prepare step (which attaches DeploymentTargetAnnotations) is still running.
            dependsOn: (string[])[WellKnownPipelineSteps.Push, WellKnownPipelineSteps.BeforeStart],
            requiredBy: WellKnownPipelineSteps.Deploy);

        using var app = builder.Build();
        await PipelineSpikeHarness.ExecutePipelineAsync(app);

        // Every native execution step was suppressed (ran as a same-name no-op clone)...
        Assert.Contains("suppressed:validate-azure-login", log);
        Assert.Contains("suppressed:create-provisioning-context", log);
        Assert.Contains("suppressed:provision-azure-bicep-resources", log);
        Assert.Contains("suppressed:provision-aca-env", log);
        Assert.Contains(log, entry => entry.StartsWith("suppressed:provision-") && entry.Contains("acr"));
        Assert.Contains(log, entry => entry.StartsWith("suppressed:login-to-acr-"));
        Assert.Contains(log, entry => entry.StartsWith("suppressed:destroy-azure-"));

        // ...while the modeling steps ran for real, materializing the provisioning tree...
        Assert.Contains("ran:azure-prepare-resources", log);
        Assert.Contains("ran:prepare-azure-container-apps-aca-env", log);

        // ...and the spliced stub walked the complete model: environment Bicep, ACR Bicep, and the
        // per-compute deployment target with its own Bicep template.
        Assert.Contains("bicep:aca-env", walked);
        Assert.Contains(walked, entry => entry.StartsWith("bicep:") && entry.Contains("acr"));
        Assert.Contains("target:web:web-containerapp", walked);
        Assert.Contains("target-bicep:web-containerapp", walked);

        if (Directory.Exists(outputPath))
        {
            Directory.Delete(outputPath, recursive: true);
        }
    }

    [Fact]
    public async Task DeploymentTargetBicep_ExposesContainerImageParameter()
    {
        // The registry/build ordering contract: Aspire's build/push steps produce the image, and the walked
        // deployment-target Bicep must expose the parameter the Pulumi backend feeds the pushed image into.
        var builder = PipelineSpikeHarness.CreatePublishBuilder(out var outputPath);
        builder.AddAzureContainerAppEnvironment("aca-env");
        builder.AddContainer("web", "nginx:latest").WithHttpEndpoint(targetPort: 80);

        PipelineSpikeHarness.WrapNativeSteps(
            builder,
            resource => resource is AzureBicepResource || resource.GetType().Name == "AzureEnvironmentResource",
            step => IsAzureExecutionStep(step) || IsSandboxOnlyStep(step));

        string? targetParameters = null;
        builder.Pipeline.AddStep(
            "pulumi-stub-inspect",
            async context =>
            {
                var web = context.Model.Resources.Single(r => r.Name == "web");
                var annotation = web.Annotations.OfType<DeploymentTargetAnnotation>().Single();
                var targetBicep = Assert.IsAssignableFrom<AzureBicepResource>(annotation.DeploymentTarget);
                var template = await File.ReadAllTextAsync(targetBicep.GetBicepTemplateFile().Path, context.CancellationToken);
                targetParameters = string.Join(";", targetBicep.Parameters.Keys) + "|" + template;
            },
            dependsOn: (string[])[WellKnownPipelineSteps.Push, WellKnownPipelineSteps.BeforeStart],
            requiredBy: WellKnownPipelineSteps.Deploy);

        using var app = builder.Build();
        await PipelineSpikeHarness.ExecutePipelineAsync(app);

        Assert.NotNull(targetParameters);
        // The image flows in via a parameter (the exact name is asserted loosely: it must reference an image).
        Assert.Contains("image", targetParameters, StringComparison.OrdinalIgnoreCase);

        if (Directory.Exists(outputPath))
        {
            Directory.Delete(outputPath, recursive: true);
        }
    }
}
