// Licensed under the MIT License.

using System.Collections.Concurrent;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Pipelines;
using Xunit;

namespace EmmittJ.Aspire.Hosting.Pulumi.NativeAdoptionSpike.Tests;

/// <summary>
/// End-to-end proof of the shipping adoption primitives: <see cref="NativePipelineStepAdoption"/> with
/// <see cref="PulumiStepSuppressionSelector.AzureContainerApps"/> neutralizes the native execution steps,
/// a spliced Pulumi-slot step observes the fully materialized provisioning model, and the pipeline
/// completes cleanly without any cloud CLI.
/// </summary>
public class ProductionAdoptionEndToEndTests
{
    [Fact]
    public async Task SuppressExecutionSteps_AcaPipelineCompletes_SplicedStepWalksModel()
    {
        var builder = PipelineSpikeHarness.CreatePublishBuilder(out var outputPath);
        builder.AddAzureContainerAppEnvironment("aca-env");
        builder.AddContainer("web", "nginx:latest").WithHttpEndpoint(targetPort: 80);

        // Production path: the shipping selector suppresses the native execution steps.
        NativePipelineStepAdoption.SuppressExecutionSteps(
            builder,
            resource => resource is AzureBicepResource || resource.GetType().Name == "AzureEnvironmentResource",
            PulumiStepSuppressionSelector.AzureContainerApps);

        // Sandbox-only neutralization (not part of the adoption seam): no docker build/push or Azure CLI is
        // available here. In a real deployment these steps keep running under Aspire.
        NativePipelineStepAdoption.SuppressExecutionSteps(
            builder,
            _ => true,
            new PulumiStepSuppressionSelector
            {
                StepNamePrefixes = ["publish-azure", "print-dashboard-url-"],
                Tags = [WellKnownPipelineTags.BuildCompute, WellKnownPipelineTags.PushContainerImage],
            });

        var walked = new ConcurrentQueue<string>();
        builder.Pipeline.AddStep(
            "pulumi-deploy",
            async context =>
            {
                foreach (var resource in context.Model.Resources)
                {
                    if (resource.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var annotation)
                        && annotation.DeploymentTarget is AzureBicepResource targetBicep)
                    {
                        var template = await File.ReadAllTextAsync(targetBicep.GetBicepTemplateFile().Path, context.CancellationToken);
                        Assert.Contains("containerApp", template);
                        walked.Enqueue($"target:{resource.Name}:{annotation.DeploymentTarget.Name}");
                    }
                }
            },
            // The before-start edge is required: without it the scheduler may start this step while the
            // native prepare step (which attaches DeploymentTargetAnnotations) is still running.
            dependsOn: (string[])[WellKnownPipelineSteps.Push, WellKnownPipelineSteps.BeforeStart],
            requiredBy: WellKnownPipelineSteps.Deploy);

        using var app = builder.Build();
        await PipelineSpikeHarness.ExecutePipelineAsync(app);

        Assert.Contains("target:web:web-containerapp", walked);

        if (Directory.Exists(outputPath))
        {
            Directory.Delete(outputPath, recursive: true);
        }
    }
}
