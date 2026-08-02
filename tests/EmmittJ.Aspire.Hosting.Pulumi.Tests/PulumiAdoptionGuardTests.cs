// Licensed under the MIT License.

#pragma warning disable ASPIREPIPELINES001 // Pipeline APIs are experimental
#pragma warning disable ASPIREPIPELINES002 // Pipeline tag APIs are experimental

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EmmittJ.Aspire.Hosting.Pulumi.Tests;

/// <summary>
/// Tests for the configuration-time adoption guard <c>PublishAsPulumi</c> wires onto the Pulumi environment:
/// suppression happens at builder time, so execution steps registered <em>after</em> adoption would silently
/// run alongside Pulumi — the guard must turn that drift into an actionable pipeline failure while letting
/// suppressed clones, kept steps, and cosmetic steps pass.
/// </summary>
public class PulumiAdoptionGuardTests
{
    [Fact]
    public async Task Guard_PassesWhenAllExecutionStepsWereSuppressed()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        var environment = builder.AddResource(new TestComputeEnvironmentResource("native-env"));
        AddStepAnnotation(environment.Resource, CreateStep("native-provision", [], [], WellKnownPipelineTags.ProvisionInfrastructure));

        environment.PublishAsPulumi(PulumiStepSuppressionSelector.Structural, _ => Task.CompletedTask);

        using var app = builder.Build();

        // The provision step existed at adoption time, so it was suppressed (tracked no-op clone): no error.
        await RunGuardAsync(app);
    }

    [Fact]
    public async Task Guard_FailsWhenExecutionStepIsRegisteredAfterAdoption()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        var environment = builder.AddResource(new TestComputeEnvironmentResource("native-env"));

        environment.PublishAsPulumi(PulumiStepSuppressionSelector.Structural, _ => Task.CompletedTask);

        // Simulates an integration that registers an execution step after PublishAsPulumi ran: suppression
        // (builder time) can no longer wrap it, so only the guard stands between it and a double deploy.
        AddStepAnnotation(environment.Resource, CreateStep("rogue-provision", [], [], WellKnownPipelineTags.ProvisionInfrastructure));

        using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => RunGuardAsync(app));
        Assert.Contains("rogue-provision", exception.Message);
        Assert.Contains("native-env", exception.Message);
        Assert.Contains("PublishAsPulumi", exception.Message);
    }

    [Fact]
    public async Task Guard_FailsWhenUnrecognizedStepSitsInsideDeployPhase()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        var environment = builder.AddResource(new TestComputeEnvironmentResource("native-env"));

        environment.PublishAsPulumi(PulumiStepSuppressionSelector.Structural, _ => Task.CompletedTask);

        // A step that is only *transitively* inside deploy-prereq → deploy: no structural rule matches it
        // directly, but the phase reachability analysis must still flag it.
        AddStepAnnotation(environment.Resource, CreateStep("custom-deployer", ["intermediate-step"], [WellKnownPipelineSteps.Deploy]));

        using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => RunGuardAsync(
            app,
            extraSteps: [CreateStep("intermediate-step", [WellKnownPipelineSteps.DeployPrereq], [])]));
        Assert.Contains("custom-deployer", exception.Message);
        Assert.Contains("deploy or destroy phase", exception.Message);
    }

    [Fact]
    public async Task Guard_AllowsKeptSteps()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        var environment = builder.AddResource(new TestComputeEnvironmentResource("native-env"));

        environment.PublishAsPulumi(
            PulumiStepSuppressionSelector.Structural with { KeepStepNames = ["intentional-provision"] },
            _ => Task.CompletedTask);

        // The user declared this execution step intentionally kept: neither suppression nor the guard touch it.
        AddStepAnnotation(environment.Resource, CreateStep("intentional-provision", [], [], WellKnownPipelineTags.ProvisionInfrastructure));

        using var app = builder.Build();

        await RunGuardAsync(app);
    }

    [Fact]
    public async Task Guard_AllowsCosmeticStepsInsideDeployPhase()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        var environment = builder.AddResource(new TestComputeEnvironmentResource("native-env"));

        environment.PublishAsPulumi(PulumiStepSuppressionSelector.Structural, _ => Task.CompletedTask);

        // Dashboard/summary printers legitimately sit inside the deploy phase (like the native
        // print-dashboard-url step); they keep running under adoption.
        AddStepAnnotation(environment.Resource, CreateStep("print-endpoints", ["intermediate-step"], [WellKnownPipelineSteps.Deploy], "print-summary"));

        using var app = builder.Build();

        await RunGuardAsync(
            app,
            extraSteps: [CreateStep("intermediate-step", [WellKnownPipelineSteps.DeployPrereq], [])]);
    }

    [Fact]
    public async Task Guard_IgnoresResourcesOutsideTheSuppressionFilter()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        var environment = builder.AddResource(new TestComputeEnvironmentResource("native-env"));
        var other = builder.AddResource(new TestResource("other-env"));

        environment.PublishAsPulumi(
            PulumiStepSuppressionSelector.Structural,
            _ => Task.CompletedTask,
            suppressionResourceFilter: resource => ReferenceEquals(resource, environment.Resource));

        // Steps of resources outside the adoption filter (for example a second, non-adopted environment)
        // are none of this Pulumi environment's business.
        AddStepAnnotation(other.Resource, CreateStep("other-provision", [], [], WellKnownPipelineTags.ProvisionInfrastructure));

        using var app = builder.Build();

        await RunGuardAsync(app);
    }

    /// <summary>
    /// Resolves every resource's steps (mimicking the pipeline: assigns <see cref="PipelineStep.Resource"/>),
    /// then invokes the guard callback <c>PublishAsPulumi</c> attached to the Pulumi environment with a
    /// configuration context, exactly as the real pipeline does after all step factories have run.
    /// </summary>
    private static async Task RunGuardAsync(DistributedApplication app, IEnumerable<PipelineStep>? extraSteps = null)
    {
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var pipelineContext = new PipelineContext(
            model,
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
            app.Services,
            NullLogger.Instance,
            CancellationToken.None);

        var steps = new List<PipelineStep>(extraSteps ?? []);
        foreach (var resource in model.Resources)
        {
            foreach (var annotation in resource.Annotations.OfType<PipelineStepAnnotation>().ToList())
            {
                var resolved = await annotation.CreateStepsAsync(new PipelineStepFactoryContext
                {
                    PipelineContext = pipelineContext,
                    Resource = resource,
                });
                foreach (var step in resolved)
                {
                    step.Resource ??= resource;
                    steps.Add(step);
                }
            }
        }

        var pulumiEnvironment = model.Resources.OfType<PulumiEnvironmentResource>().Single();
        var guard = pulumiEnvironment.Annotations.OfType<PipelineConfigurationAnnotation>().Single();
        await guard.Callback(new PipelineConfigurationContext
        {
            Model = model,
            Steps = steps,
            Services = app.Services,
        });
    }

    private static void AddStepAnnotation(IResource resource, PipelineStep step) =>
        resource.Annotations.Add(new PipelineStepAnnotation(_ => Task.FromResult<IEnumerable<PipelineStep>>([step])));

    private static PipelineStep CreateStep(string name, string[] dependsOn, string[] requiredBy, params string[] tags) => new()
    {
        Name = name,
        Action = _ => Task.CompletedTask,
        DependsOnSteps = [.. dependsOn],
        RequiredBySteps = [.. requiredBy],
        Tags = [.. tags],
    };
}
