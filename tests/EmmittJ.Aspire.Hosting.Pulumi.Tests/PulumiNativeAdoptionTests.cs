// Licensed under the MIT License.

#pragma warning disable ASPIREPIPELINES001 // Pipeline APIs are experimental
#pragma warning disable ASPIRECOMPUTE001  // Compute resource APIs are experimental
#pragma warning disable ASPIRECOMPUTE002  // IComputeEnvironmentResource is experimental

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EmmittJ.Aspire.Hosting.Pulumi.Tests;

/// <summary>
/// Tests for the adopt-and-traverse decorator (<c>PublishAsPulumi</c>) and the generic
/// <see cref="PulumiBackendResource"/> it registers.
/// </summary>
public class PulumiNativeAdoptionTests
{
    [Fact]
    public void PublishAsPulumi_InPublishMode_AddsBackendResource()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        var environment = builder.AddResource(new TestComputeEnvironmentResource("native-env"));

        environment.PublishAsPulumi(
            new PulumiStepSuppressionSelector { Tags = ["provision-infra"] },
            _ => Task.CompletedTask);

        var backend = Assert.Single(builder.Resources.OfType<PulumiBackendResource>());
        Assert.Equal("native-env-pulumi", backend.Name);
        Assert.Same(environment.Resource, backend.AdoptedEnvironment);
        Assert.Equal("native-env-pulumi", backend.PulumiProjectName);
    }

    [Fact]
    public void PublishAsPulumi_InRunMode_IsNoOp()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var environment = builder.AddResource(new TestComputeEnvironmentResource("native-env"));

        var annotationsBefore = environment.Resource.Annotations.Count;

        environment.PublishAsPulumi(
            PulumiStepSuppressionSelector.AzureContainerApps,
            _ => Task.CompletedTask);

        // Local development stays untouched: no backend resource, no wrapped annotations.
        Assert.Empty(builder.Resources.OfType<PulumiBackendResource>());
        Assert.Equal(annotationsBefore, environment.Resource.Annotations.Count);
    }

    [Fact]
    public void PublishAsPulumi_CalledTwiceForSameEnvironment_Throws()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        var environment = builder.AddResource(new TestComputeEnvironmentResource("native-env"));

        environment.PublishAsPulumi(PulumiStepSuppressionSelector.Kubernetes, _ => Task.CompletedTask);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            environment.PublishAsPulumi(PulumiStepSuppressionSelector.Kubernetes, _ => Task.CompletedTask));
        Assert.Contains("native-env", exception.Message);
    }

    [Fact]
    public async Task PublishAsPulumi_SuppressesMatchingNativeExecutionSteps()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        var environment = builder.AddResource(new TestComputeEnvironmentResource("native-env"));

        var nativeRanSteps = new List<string>();
        environment.Resource.Annotations.Add(new PipelineStepAnnotation(_ => Task.FromResult<IEnumerable<PipelineStep>>(
        [
            new PipelineStep
            {
                Name = "native-provision",
                Action = _ => { nativeRanSteps.Add("native-provision"); return Task.CompletedTask; },
                Tags = ["provision-infra"],
            },
            new PipelineStep
            {
                Name = "native-prepare",
                Action = _ => { nativeRanSteps.Add("native-prepare"); return Task.CompletedTask; },
            },
        ])));

        environment.PublishAsPulumi(
            new PulumiStepSuppressionSelector { Tags = ["provision-infra"] },
            _ => Task.CompletedTask);

        using var app = builder.Build();
        var steps = await ResolveStepsAsync(app, environment.Resource);
        var context = CreateStepContext(app);

        foreach (var step in steps)
        {
            await step.Action(context);
        }

        // The execution step became a no-op; the modeling step still ran the original action.
        Assert.Equal(["native-prepare"], nativeRanSteps);
    }

    [Fact]
    public async Task BackendResource_RegistersExpectedLifecycleSteps()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        var environment = builder.AddResource(new TestComputeEnvironmentResource("native-env"));

        environment.PublishAsPulumi(PulumiStepSuppressionSelector.DockerCompose, _ => Task.CompletedTask);

        using var app = builder.Build();
        var backend = builder.Resources.OfType<PulumiBackendResource>().Single();
        var steps = await ResolveStepsAsync(app, backend);

        var publish = steps.Single(s => s.Name == "pulumi-publish-native-env-pulumi");
        Assert.Contains(WellKnownPipelineSteps.PublishPrereq, publish.DependsOnSteps);
        Assert.Contains(WellKnownPipelineSteps.Publish, publish.RequiredBySteps);

        var deploy = steps.Single(s => s.Name == "pulumi-deploy-native-env-pulumi");
        // Deploy must run after images are pushed, and after before-start so it never observes a
        // half-materialized model (native prepare steps attach DeploymentTargetAnnotations concurrently
        // otherwise).
        Assert.Contains(WellKnownPipelineSteps.Push, deploy.DependsOnSteps);
        Assert.Contains(WellKnownPipelineSteps.BeforeStart, deploy.DependsOnSteps);
        Assert.Contains(WellKnownPipelineSteps.Deploy, deploy.RequiredBySteps);
        Assert.Contains(PulumiPipelineSteps.PulumiTag, deploy.Tags);

        var destroy = steps.Single(s => s.Name == "pulumi-destroy-native-env-pulumi");
        Assert.Contains(WellKnownPipelineSteps.DestroyPrereq, destroy.DependsOnSteps);
        Assert.Contains(WellKnownPipelineSteps.Destroy, destroy.RequiredBySteps);
    }

    [Fact]
    public void AdoptionContext_GetDeploymentTargets_ReturnsTargetsForAdoptedEnvironmentOnly()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        var environment = builder.AddResource(new TestComputeEnvironmentResource("native-env"));
        var otherEnvironment = builder.AddResource(new TestComputeEnvironmentResource("other-env"));
        var compute = builder.AddContainer("web", "nginx:latest");
        var otherCompute = builder.AddContainer("api", "nginx:latest");

        environment.PublishAsPulumi(PulumiStepSuppressionSelector.Kubernetes, _ => Task.CompletedTask);

        // Simulate what the native environment's prepare step does: attach deployment targets.
        var adoptedTarget = new TestResource("web-target");
        compute.Resource.Annotations.Add(new DeploymentTargetAnnotation(adoptedTarget)
        {
            ComputeEnvironment = environment.Resource,
        });
        otherCompute.Resource.Annotations.Add(new DeploymentTargetAnnotation(new TestResource("api-target"))
        {
            ComputeEnvironment = otherEnvironment.Resource,
        });

        using var app = builder.Build();
        var backend = builder.Resources.OfType<PulumiBackendResource>().Single();

        var context = new PulumiAdoptionContext(
            app.Services.GetRequiredService<DistributedApplicationModel>(),
            backend,
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
            app.Services,
            NullLogger.Instance,
            CancellationToken.None);

        var (targetedCompute, annotation) = Assert.Single(context.GetDeploymentTargets());
        Assert.Equal("web", targetedCompute.Name);
        Assert.Same(adoptedTarget, annotation.DeploymentTarget);
    }

    [Fact]
    public void BackendResource_StackName_UsesOverrideThenAspireEnvironment()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        var environment = builder.AddResource(new TestComputeEnvironmentResource("native-env"));
        environment.PublishAsPulumi(PulumiStepSuppressionSelector.DockerCompose, _ => Task.CompletedTask);

        using var app = builder.Build();
        var backend = builder.Resources.OfType<PulumiBackendResource>().Single();

        backend.StackNameOverride = "prod";
        Assert.Equal("prod", backend.ResolveStackName(app.Services));
    }

    private static async Task<List<PipelineStep>> ResolveStepsAsync(DistributedApplication app, IResource resource)
    {
        var pipelineContext = CreatePipelineContext(app);

        var steps = new List<PipelineStep>();
        foreach (var annotation in resource.Annotations.OfType<PipelineStepAnnotation>().ToList())
        {
            var factoryContext = new PipelineStepFactoryContext
            {
                PipelineContext = pipelineContext,
                Resource = resource,
            };
            steps.AddRange(await annotation.CreateStepsAsync(factoryContext));
        }

        return steps;
    }

    private static PipelineContext CreatePipelineContext(DistributedApplication app) => new(
        app.Services.GetRequiredService<DistributedApplicationModel>(),
        app.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
        app.Services,
        NullLogger.Instance,
        CancellationToken.None);

    private static PipelineStepContext CreateStepContext(DistributedApplication app) => new()
    {
        PipelineContext = CreatePipelineContext(app),
        ReportingStep = new NoOpReportingStep(),
    };

    /// <summary>A minimal native compute environment stand-in for adoption tests.</summary>
    private sealed class TestComputeEnvironmentResource(string name) : Resource(name), IComputeEnvironmentResource;

    private sealed class NoOpReportingStep : IReportingStep
    {
        public Task<IReportingTask> CreateTaskAsync(string description, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Suppressed steps must not report tasks.");

        public Task<IReportingTask> CreateTaskAsync(MarkdownString description, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Suppressed steps must not report tasks.");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task CompleteAsync(string completionMessage, CompletionState completionState, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task CompleteAsync(MarkdownString completionMessage, CompletionState completionState, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public void Log(Microsoft.Extensions.Logging.LogLevel logLevel, string message, bool enableMarkdown) { }

        public void Log(Microsoft.Extensions.Logging.LogLevel logLevel, string message) { }

        public void Log(Microsoft.Extensions.Logging.LogLevel logLevel, MarkdownString message) { }
    }
}
