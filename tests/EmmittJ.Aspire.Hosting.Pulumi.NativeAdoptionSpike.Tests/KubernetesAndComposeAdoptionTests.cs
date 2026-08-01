// Licensed under the MIT License.

using System.Collections.Concurrent;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EmmittJ.Aspire.Hosting.Pulumi.NativeAdoptionSpike.Tests;

/// <summary>
/// Validates the same suppress-and-splice seam proven for Azure Container Apps against the Kubernetes and
/// Docker Compose native environments: their execution steps (Helm, docker compose up/down) are replaced by
/// same-name no-op clones, the modeling/prepare steps keep running, and a spliced stub step observes the
/// deployment targets each environment materialized.
/// </summary>
public class KubernetesAndComposeAdoptionTests
{
    private static bool IsKubernetesExecutionStep(PipelineStep step) =>
        // The production selector is the system under test: it must classify exactly the execution steps.
        PulumiStepSuppressionSelector.Kubernetes.Matches(step);

    private static bool IsComposeExecutionStep(PipelineStep step) =>
        PulumiStepSuppressionSelector.DockerCompose.Matches(step);

    private static bool IsSandboxOnlyStep(PipelineStep step) =>
        // Neutralized only so the sandbox needs no docker daemon for image builds; in a real deployment
        // build/push keeps running under Aspire ahead of the Pulumi backend's deploy step.
        step.Tags.Contains(WellKnownPipelineTags.BuildCompute)
        || step.Tags.Contains(WellKnownPipelineTags.PushContainerImage);

    [Fact]
    public async Task Kubernetes_SuppressedHelmDeploy_StubWalksDeploymentTargets()
    {
        var builder = PipelineSpikeHarness.CreatePublishBuilder(out var outputPath);
        var environment = builder.AddKubernetesEnvironment("k8s-env");
        builder.AddContainer("web", "nginx:latest").WithHttpEndpoint(targetPort: 80);

        var log = PipelineSpikeHarness.WrapNativeSteps(
            builder,
            resource => ReferenceEquals(resource, environment.Resource),
            step => IsKubernetesExecutionStep(step) || IsSandboxOnlyStep(step));

        var walked = new ConcurrentQueue<string>();
        builder.Pipeline.AddStep(
            "pulumi-stub-deploy",
            context =>
            {
                foreach (var resource in context.Model.Resources)
                {
                    if (resource.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var annotation))
                    {
                        walked.Enqueue($"target:{resource.Name}:{annotation.DeploymentTarget.GetType().Name}");
                    }
                }

                return Task.CompletedTask;
            },
            dependsOn: (string[])[WellKnownPipelineSteps.Push, WellKnownPipelineSteps.BeforeStart],
            requiredBy: WellKnownPipelineSteps.Deploy);

        using var app = builder.Build();
        await PipelineSpikeHarness.ExecutePipelineAsync(app);

        // Helm never ran; the modeling steps did.
        Assert.Contains("suppressed:helm-deploy-k8s-env", log);
        Assert.Contains("suppressed:helm-uninstall-k8s-env", log);
        Assert.Contains("suppressed:check-helm-prereqs-k8s-env", log);
        Assert.Contains("suppressed:destroy-helm-k8s-env", log);
        Assert.Contains("ran:prepare-deployment-targets-k8s-env", log);
        Assert.Contains("ran:publish-k8s-env", log);

        // The environment materialized a Kubernetes deployment target for the compute resource.
        Assert.Contains(walked, entry => entry.StartsWith("target:web:"));

        // The publish artifact (the walked source model in serialized form) was still produced.
        Assert.True(Directory.Exists(outputPath) && Directory.EnumerateFiles(outputPath, "*", SearchOption.AllDirectories).Any());
        Directory.Delete(outputPath, recursive: true);
    }

    [Fact]
    public async Task DockerCompose_SuppressedComposeUp_StubWalksDeploymentTargets()
    {
        var builder = PipelineSpikeHarness.CreatePublishBuilder(out var outputPath);
        var environment = builder.AddDockerComposeEnvironment("compose-env");
        builder.AddContainer("web", "nginx:latest").WithHttpEndpoint(targetPort: 80);

        var log = PipelineSpikeHarness.WrapNativeSteps(
            builder,
            resource => ReferenceEquals(resource, environment.Resource),
            step => IsComposeExecutionStep(step) || IsSandboxOnlyStep(step));

        var walked = new ConcurrentQueue<string>();
        builder.Pipeline.AddStep(
            "pulumi-stub-deploy",
            context =>
            {
                foreach (var resource in context.Model.Resources)
                {
                    if (resource.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var annotation))
                    {
                        walked.Enqueue($"target:{resource.Name}:{annotation.DeploymentTarget.GetType().Name}");
                    }
                }

                return Task.CompletedTask;
            },
            dependsOn: (string[])[WellKnownPipelineSteps.Push, WellKnownPipelineSteps.BeforeStart],
            requiredBy: WellKnownPipelineSteps.Deploy);

        using var app = builder.Build();
        await PipelineSpikeHarness.ExecutePipelineAsync(app);

        // docker compose up/down never ran; the modeling steps did.
        Assert.Contains("suppressed:docker-compose-up-compose-env", log);
        Assert.Contains("suppressed:docker-compose-down-compose-env", log);
        Assert.Contains("suppressed:destroy-compose-compose-env", log);
        Assert.Contains("ran:prepare-deployment-targets-compose-env", log);
        Assert.Contains("ran:publish-compose-env", log);

        // The environment materialized a compose deployment target for the compute resource.
        Assert.Contains(walked, entry => entry.StartsWith("target:web:"));

        if (Directory.Exists(outputPath))
        {
            Directory.Delete(outputPath, recursive: true);
        }
    }
}
