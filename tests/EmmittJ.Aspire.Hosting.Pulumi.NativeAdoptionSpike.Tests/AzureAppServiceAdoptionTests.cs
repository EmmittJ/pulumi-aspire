// Licensed under the MIT License.

using System.Collections.Concurrent;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Pipelines;
using Xunit;

namespace EmmittJ.Aspire.Hosting.Pulumi.NativeAdoptionSpike.Tests;

/// <summary>
/// End-to-end proof of the adoption seam for the native Azure App Service environment
/// (<c>AddAzureAppServiceEnvironment</c>): the execution steps are suppressed via
/// <see cref="PulumiStepSuppressionSelector.AzureAppService"/>, a stub "Pulumi backend" step is spliced into
/// the deploy slot, and the full pipeline executes in-process. Success = the stub observes the fully
/// materialized provisioning model (environment Bicep, ACR Bicep, and the per-compute website
/// deployment-target Bicep) while no native execution step ran for real.
/// </summary>
/// <remarks>
/// <para>
/// Two App Service-specific wrinkles keep this from being a wholesale copy of the ACA adoption tests
/// (see the App Service section of docs/spikes/native-environment-step-adoption.md):
/// </para>
/// <list type="number">
/// <item>The native prepare step only materializes <see cref="DeploymentTargetAnnotation"/>s for project
/// resources and containers with a <see cref="DockerfileBuildAnnotation"/> — a plain image container (the
/// ACA tests use <c>AddContainer("web", "nginx:latest")</c>) is silently skipped, so these tests use
/// <c>AddDockerfile</c>.</item>
/// <item>Because a Dockerfile container requires image push, Aspire's built-in <c>push-prereq</c> step
/// validates registry availability — and it has no dependency edge onto <c>before-start</c>, so under the
/// harness's single-DAG execution it can race ahead of <c>prepare-azure-app-service-*</c> (which attaches
/// the registry-bearing annotation). Real <c>aspire deploy</c> runs the before-start phase first, so the
/// harness adds the missing edge explicitly.</item>
/// </list>
/// </remarks>
public class AzureAppServiceAdoptionTests
{
    private static bool IsAzureExecutionStep(PipelineStep step) =>
        // The production selector is the system under test: it must classify exactly the execution steps.
        PulumiStepSuppressionSelector.AzureAppService.Matches(step);

    private static bool IsSandboxOnlyStep(PipelineStep step) =>
        // Not part of the adoption seam: neutralized only so the sandbox run needs no docker build/push
        // or Azure CLI. In a real deployment these steps keep running (build/push stays with Aspire).
        step.Tags.Contains(WellKnownPipelineTags.BuildCompute)
        || step.Tags.Contains(WellKnownPipelineTags.PushContainerImage)
        || step.Name.StartsWith("publish-azure", StringComparison.Ordinal)
        || step.Name.StartsWith("print-dashboard-url-", StringComparison.Ordinal);

    /// <summary>
    /// Creates a publish-mode builder with an App Service environment and a Dockerfile-built container
    /// (the compute shape App Service materializes deployment targets for).
    /// </summary>
    private static IDistributedApplicationBuilder CreateAppServiceBuilder(out string outputPath, out string dockerfileContextPath)
    {
        var builder = PipelineSpikeHarness.CreatePublishBuilder(out outputPath);
        builder.AddAzureAppServiceEnvironment("appsvc-env");

        // A plain AddContainer image is silently skipped by prepare-azure-app-service-*; only projects and
        // Dockerfile-built containers get a DeploymentTargetAnnotation.
        dockerfileContextPath = Path.Combine(Path.GetTempPath(), $"appsvc-adoption-web-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dockerfileContextPath);
        File.WriteAllText(Path.Combine(dockerfileContextPath, "Dockerfile"), "FROM nginx:latest\n");
        builder.AddDockerfile("web", dockerfileContextPath)
            .WithHttpEndpoint(targetPort: 80, env: "PORT")
            .WithExternalHttpEndpoints();

        // Harness-only ordering edge: the in-process harness executes the whole step DAG at once, whereas
        // real operations run the before-start phase (which includes prepare-azure-app-service-*) to
        // completion first. Without this edge Aspire's built-in push-prereq step can run before the prepare
        // step attaches the registry-bearing DeploymentTargetAnnotation and fail with "no container
        // registry is available".
        builder.Pipeline.AddStep(
            "order-push-prereq-after-before-start",
            _ => Task.CompletedTask,
            dependsOn: WellKnownPipelineSteps.BeforeStart,
            requiredBy: WellKnownPipelineSteps.PushPrereq);

        return builder;
    }

    [Fact]
    public async Task SuppressedNativeDeploy_StubStepWalksProvisioningModel()
    {
        var builder = CreateAppServiceBuilder(out var outputPath, out var dockerfileContextPath);

        var log = PipelineSpikeHarness.WrapNativeSteps(
            builder,
            resource => resource is AzureBicepResource || resource.GetType().Name == "AzureEnvironmentResource",
            step => IsAzureExecutionStep(step) || IsSandboxOnlyStep(step));

        // Sandbox-only: no docker daemon is available for the Dockerfile container's build/push steps.
        PipelineSpikeHarness.WrapNativeSteps(
            builder,
            resource => resource.Name == "web",
            step => step.Tags.Contains(WellKnownPipelineTags.BuildCompute) || step.Tags.Contains(WellKnownPipelineTags.PushContainerImage));

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
                            Assert.Contains("Microsoft.Web/sites", template);
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
        Assert.Contains("suppressed:provision-appsvc-env", log);
        Assert.Contains(log, entry => entry.StartsWith("suppressed:provision-") && entry.Contains("acr"));
        Assert.Contains(log, entry => entry.StartsWith("suppressed:login-to-acr-"));
        Assert.Contains(log, entry => entry.StartsWith("suppressed:destroy-azure-"));

        // ...while the modeling steps ran for real, materializing the provisioning tree...
        Assert.Contains("ran:azure-prepare-resources", log);
        Assert.Contains("ran:prepare-azure-app-service-appsvc-env", log);
        Assert.Contains("ran:validate-appservice-config-appsvc-env", log);

        // ...and the spliced stub walked the complete model: environment Bicep, ACR Bicep, and the
        // per-compute website deployment target with its own Bicep template.
        Assert.Contains("bicep:appsvc-env", walked);
        Assert.Contains(walked, entry => entry.StartsWith("bicep:") && entry.Contains("acr"));
        Assert.Contains("target:web:web-website", walked);
        Assert.Contains("target-bicep:web-website", walked);

        Cleanup(outputPath, dockerfileContextPath);
    }

    [Fact]
    public async Task PlainImageContainer_GetsNoDeploymentTarget()
    {
        // Pins the App Service-specific gap that keeps adoption from being a wholesale copy of ACA: the
        // native prepare step only creates websites for projects and Dockerfile-built containers. A plain
        // image container silently gets no DeploymentTargetAnnotation, so a Pulumi program walking
        // GetDeploymentTargets would never see it. If an Aspire version bump lifts this restriction, this
        // test fails and the documentation can drop the caveat.
        var builder = PipelineSpikeHarness.CreatePublishBuilder(out var outputPath);
        builder.AddAzureAppServiceEnvironment("appsvc-env");
        builder.AddContainer("web", "nginx:latest").WithHttpEndpoint(targetPort: 80);

        PipelineSpikeHarness.WrapNativeSteps(
            builder,
            resource => resource is AzureBicepResource || resource.GetType().Name == "AzureEnvironmentResource",
            step => IsAzureExecutionStep(step) || IsSandboxOnlyStep(step));

        IResourceAnnotation? observed = new DeploymentTargetAnnotation(new ContainerResource("sentinel"));
        builder.Pipeline.AddStep(
            "pulumi-stub-deploy",
            context =>
            {
                var web = context.Model.Resources.Single(r => r.Name == "web");
                observed = web.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var annotation) ? annotation : null;
                return Task.CompletedTask;
            },
            dependsOn: (string[])[WellKnownPipelineSteps.Push, WellKnownPipelineSteps.BeforeStart],
            requiredBy: WellKnownPipelineSteps.Deploy);

        using var app = builder.Build();
        await PipelineSpikeHarness.ExecutePipelineAsync(app);

        Assert.Null(observed);

        Cleanup(outputPath);
    }

    private static void Cleanup(params string[] paths)
    {
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }
}
