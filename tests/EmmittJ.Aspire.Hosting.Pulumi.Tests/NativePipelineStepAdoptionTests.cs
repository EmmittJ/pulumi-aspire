// Licensed under the MIT License.

#pragma warning disable ASPIREPIPELINES001 // Pipeline APIs are experimental

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EmmittJ.Aspire.Hosting.Pulumi.Tests;

/// <summary>
/// Tests for the production adopt-and-traverse primitives promoted from the native-adoption spike
/// (docs/spikes/native-environment-step-adoption.md): the data-driven suppression selector and the
/// wrap-at-builder-time step suppression.
/// </summary>
public class NativePipelineStepAdoptionTests
{
    private static PipelineStep CreateStep(string name, params string[] tags) => new()
    {
        Name = name,
        Action = _ => Task.CompletedTask,
        Tags = [.. tags],
    };

    [Fact]
    public void Selector_MatchesByExactName_Prefix_AndTag()
    {
        var selector = new PulumiStepSuppressionSelector
        {
            StepNames = ["exact-step"],
            StepNamePrefixes = ["destroy-"],
            Tags = ["provision-infra"],
        };

        Assert.True(selector.Matches(CreateStep("exact-step")));
        Assert.True(selector.Matches(CreateStep("destroy-anything")));
        Assert.True(selector.Matches(CreateStep("other", "provision-infra")));
        Assert.False(selector.Matches(CreateStep("prepare-things", "print-summary")));
        Assert.False(selector.Matches(CreateStep("exact-step-suffixed")));
    }

    [Fact]
    public void AzureContainerAppsSelector_SuppressesExecution_KeepsModeling()
    {
        var selector = PulumiStepSuppressionSelector.AzureContainerApps;

        // Execution steps (from the pinned catalogue) must match.
        Assert.True(selector.Matches(CreateStep("validate-azure-login")));
        Assert.True(selector.Matches(CreateStep("create-provisioning-context")));
        Assert.True(selector.Matches(CreateStep("provision-azure-bicep-resources", "provision-infra")));
        Assert.True(selector.Matches(CreateStep("provision-acr", "provision-infra")));
        Assert.True(selector.Matches(CreateStep("login-to-acr-acr", "acr-login")));
        Assert.True(selector.Matches(CreateStep("destroy-azure-azure-env")));

        // Modeling steps must keep running: they materialize the model the Pulumi backend walks.
        Assert.False(selector.Matches(CreateStep("azure-prepare-resources")));
        Assert.False(selector.Matches(CreateStep("prepare-azure-container-apps-aca-env")));
        Assert.False(selector.Matches(CreateStep("publish-azure-env")));
    }

    [Fact]
    public void KubernetesSelector_SuppressesHelm_KeepsModeling()
    {
        var selector = PulumiStepSuppressionSelector.Kubernetes;

        Assert.True(selector.Matches(CreateStep("check-helm-prereqs-k8s-env")));
        Assert.True(selector.Matches(CreateStep("helm-deploy-k8s-env", "helm-deploy")));
        Assert.True(selector.Matches(CreateStep("helm-uninstall-k8s-env", "helm-uninstall")));
        Assert.True(selector.Matches(CreateStep("destroy-helm-k8s-env")));

        Assert.False(selector.Matches(CreateStep("prepare-deployment-targets-k8s-env")));
        Assert.False(selector.Matches(CreateStep("publish-k8s-env")));
    }

    [Fact]
    public void DockerComposeSelector_SuppressesComposeUpDown_KeepsModeling()
    {
        var selector = PulumiStepSuppressionSelector.DockerCompose;

        Assert.True(selector.Matches(CreateStep("docker-compose-up-compose-env", "docker-compose-up")));
        Assert.True(selector.Matches(CreateStep("docker-compose-down-compose-env", "docker-compose-down")));
        Assert.True(selector.Matches(CreateStep("destroy-compose-compose-env")));

        Assert.False(selector.Matches(CreateStep("prepare-deployment-targets-compose-env")));
        Assert.False(selector.Matches(CreateStep("publish-compose-env")));
    }

    [Fact]
    public void CloneAsNoOp_PreservesIdentityAndGraphEdges()
    {
        var resource = new TestResource("native");
        var original = new PipelineStep
        {
            Name = "provision-things",
            Description = "Provisions things.",
            Action = _ => throw new InvalidOperationException("The native execution action must not run."),
            DependsOnSteps = ["deploy-prereq"],
            RequiredBySteps = ["deploy"],
            Tags = ["provision-infra"],
            Resource = resource,
        };

        var clone = NativePipelineStepAdoption.CloneAsNoOp(original);

        // Identity and edges are preserved so the step graph stays valid and everything else schedules
        // exactly as before.
        Assert.Equal(original.Name, clone.Name);
        Assert.Equal(original.Description, clone.Description);
        Assert.Equal(original.DependsOnSteps, clone.DependsOnSteps);
        Assert.Equal(original.RequiredBySteps, clone.RequiredBySteps);
        Assert.Equal(original.Tags, clone.Tags);
        Assert.Same(original.Resource, clone.Resource);
        Assert.NotSame(original.Action, clone.Action);
    }

    [Fact]
    public async Task SuppressExecutionSteps_ReplacesMatchedSteps_PassesOthersThrough()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);

        var nativeRanSteps = new List<string>();
        var resource = new TestResource("native");
        resource.Annotations.Add(new PipelineStepAnnotation(_ => Task.FromResult<IEnumerable<PipelineStep>>(
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
        builder.AddResource(resource);

        NativePipelineStepAdoption.SuppressExecutionSteps(
            builder,
            r => ReferenceEquals(r, resource),
            new PulumiStepSuppressionSelector { Tags = ["provision-infra"] });

        using var app = builder.Build();
        var steps = await ResolveStepsAsync(app, resource);

        // The wrapped annotation still yields both steps with their identity intact.
        var provision = Assert.Single(steps, s => s.Name == "native-provision");
        var prepare = Assert.Single(steps, s => s.Name == "native-prepare");
        Assert.Contains("provision-infra", provision.Tags);

        var context = CreateStepContext(app);
        await provision.Action(context);
        await prepare.Action(context);

        // The matched execution step became a no-op; the modeling step still ran the original action.
        Assert.Equal(["native-prepare"], nativeRanSteps);
    }

    [Fact]
    public async Task SuppressExecutionSteps_IgnoresUnfilteredResources()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);

        var ranSteps = new List<string>();
        var resource = new TestResource("untouched");
        resource.Annotations.Add(new PipelineStepAnnotation(_ => Task.FromResult<IEnumerable<PipelineStep>>(
        [
            new PipelineStep
            {
                Name = "untouched-provision",
                Action = _ => { ranSteps.Add("untouched-provision"); return Task.CompletedTask; },
                Tags = ["provision-infra"],
            },
        ])));
        builder.AddResource(resource);

        NativePipelineStepAdoption.SuppressExecutionSteps(
            builder,
            _ => false,
            new PulumiStepSuppressionSelector { Tags = ["provision-infra"] });

        using var app = builder.Build();
        var steps = await ResolveStepsAsync(app, resource);

        await Assert.Single(steps).Action(CreateStepContext(app));
        Assert.Equal(["untouched-provision"], ranSteps);
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

        public void Log(LogLevel logLevel, string message, bool enableMarkdown) { }

        public void Log(LogLevel logLevel, string message) { }

        public void Log(LogLevel logLevel, MarkdownString message) { }
    }
}
