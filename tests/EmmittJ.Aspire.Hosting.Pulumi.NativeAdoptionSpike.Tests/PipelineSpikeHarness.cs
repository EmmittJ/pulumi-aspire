// Licensed under the MIT License.

using System.Collections.Concurrent;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace EmmittJ.Aspire.Hosting.Pulumi.NativeAdoptionSpike.Tests;

/// <summary>
/// Spike harness for the native-environment step-adoption experiment (see
/// docs/spikes/native-environment-step-adoption.md).
/// </summary>
/// <remarks>
/// <para>
/// Key spike finding baked into this harness: <see cref="PipelineStep.Action"/> and
/// <see cref="PipelineConfigurationContext.Steps"/> are init-only in Aspire 13.4.6, so a
/// <see cref="PipelineConfigurationAnnotation"/> callback can rewire step edges but cannot replace step
/// actions. The working suppression seam is therefore at builder time: remove each native resource's
/// <see cref="PipelineStepAnnotation"/> and re-add a wrapper whose factory invokes the original factory and
/// substitutes same-name no-op clones for the steps being suppressed. Because the clone keeps the original
/// name, tags, and DependsOn/RequiredBy edges, the step graph stays valid and every other step schedules
/// exactly as before.
/// </para>
/// </remarks>
internal static class PipelineSpikeHarness
{
    /// <summary>Creates a publish-mode builder that can execute the pipeline in a sandbox (no DCP, no cloud CLIs).</summary>
    public static IDistributedApplicationBuilder CreatePublishBuilder(out string outputPath)
    {
        outputPath = Path.Combine(Path.GetTempPath(), $"native-adoption-spike-{Guid.NewGuid():N}");
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish", "--output-path", outputPath]);

        // The pipeline's check-container-runtime step resolves DCP options even in publish mode; point them
        // at a harmless executable so pipeline execution does not require an Aspire workload install.
        builder.Configuration["DcpPublisher:CliPath"] = OperatingSystem.IsWindows() ? "cmd.exe" : "/usr/bin/true";
        builder.Configuration["DcpPublisher:DashboardPath"] = OperatingSystem.IsWindows() ? "cmd.exe" : "/usr/bin/true";
        return builder;
    }

    /// <summary>
    /// Wraps every <see cref="PipelineStepAnnotation"/> on resources matching <paramref name="resourceFilter"/> so
    /// steps matching <paramref name="suppress"/> are replaced by same-name no-op clones. Executed step names
    /// (real and suppressed) are recorded in the returned log.
    /// </summary>
    public static ConcurrentQueue<string> WrapNativeSteps(
        IDistributedApplicationBuilder builder,
        Func<IResource, bool> resourceFilter,
        Func<PipelineStep, bool> suppress)
    {
        var log = new ConcurrentQueue<string>();

        foreach (var resource in builder.Resources.Where(resourceFilter))
        {
            var originals = resource.Annotations.OfType<PipelineStepAnnotation>().ToList();
            foreach (var original in originals)
            {
                resource.Annotations.Remove(original);
                resource.Annotations.Add(new PipelineStepAnnotation(async factoryContext =>
                {
                    var steps = await original.CreateStepsAsync(factoryContext);
                    return steps.Select(step => suppress(step)
                        ? CloneWithAction(step, _ =>
                        {
                            log.Enqueue($"suppressed:{step.Name}");
                            return Task.CompletedTask;
                        })
                        : CloneWithAction(step, async ctx =>
                        {
                            log.Enqueue($"ran:{step.Name}");
                            await step.Action(ctx);
                        })).ToList();
                }));
            }
        }

        return log;
    }

    /// <summary>Clones a pipeline step, preserving its identity and graph edges but substituting the action.</summary>
    public static PipelineStep CloneWithAction(PipelineStep step, Func<PipelineStepContext, Task> action) => new()
    {
        Name = step.Name,
        Description = step.Description,
        Action = action,
        DependsOnSteps = step.DependsOnSteps,
        RequiredBySteps = step.RequiredBySteps,
        Tags = step.Tags,
        Resource = step.Resource,
    };

    /// <summary>Executes the full pipeline (publish operation covers deploy-slot steps too) in-process.</summary>
    public static async Task ExecutePipelineAsync(DistributedApplication app)
    {
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var executionContext = app.Services.GetRequiredService<DistributedApplicationExecutionContext>();
        var pipeline = app.Services.GetRequiredService<IDistributedApplicationPipeline>();

        var context = new PipelineContext(
            model,
            executionContext,
            app.Services,
            NullLogger.Instance,
            CancellationToken.None);

        await pipeline.ExecuteAsync(context);
    }

    /// <summary>Resolves the steps produced by every <see cref="PipelineStepAnnotation"/> on a resource.</summary>
    public static async Task<List<PipelineStep>> ResolveStepsAsync(DistributedApplication app, IResource resource)
    {
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var executionContext = app.Services.GetRequiredService<DistributedApplicationExecutionContext>();

        var pipelineContext = new PipelineContext(
            model,
            executionContext,
            app.Services,
            NullLogger.Instance,
            CancellationToken.None);

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
}
