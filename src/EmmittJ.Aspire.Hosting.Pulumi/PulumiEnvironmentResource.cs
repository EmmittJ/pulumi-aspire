// Licensed under the MIT License.

#pragma warning disable ASPIREPIPELINES001 // Pipeline APIs are experimental
#pragma warning disable ASPIREPIPELINES004 // IPipelineOutputService is experimental
#pragma warning disable ASPIRECOMPUTE001  // Compute resource APIs are experimental
#pragma warning disable ASPIRECOMPUTE002  // IComputeEnvironmentResource is experimental
#pragma warning disable ASPIRECOMPUTE003  // IContainerRegistry is experimental

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pulumi.Automation;

namespace EmmittJ.Aspire.Hosting.Pulumi;

/// <summary>
/// Base class for a Pulumi-managed compute environment. Deploys the compute resources targeted to it by running
/// a single inline Pulumi program through the Automation API.
/// </summary>
/// <remarks>
/// <para>
/// This resource follows the same model as Aspire's built-in compute environments (Azure Container Apps,
/// Kubernetes, Docker Compose):
/// </para>
/// <list type="bullet">
/// <item>A <c>prepare</c> pipeline step (required by <c>before-start</c>) materializes a
/// <see cref="PulumiDeploymentTargetResource"/> for each targeted compute resource and attaches a
/// <see cref="DeploymentTargetAnnotation"/>.</item>
/// <item>A <c>publish</c> step writes a reviewable <c>pulumi preview</c> artifact.</item>
/// <item>A <c>deploy</c> step (required by <c>deploy</c>, after <c>push</c>) runs <c>pulumi up</c>.</item>
/// <item>A <c>destroy</c> step (required by <c>destroy</c>) runs <c>pulumi destroy</c>.</item>
/// </list>
/// <para>
/// The environment must only be added to the application model in publish mode. Use the run/publish split
/// helper in the integration's <c>Add*</c> extension so it never surfaces as a local dashboard resource.
/// </para>
/// </remarks>
public abstract class PulumiEnvironmentResource : Resource, IComputeEnvironmentResource
{
    private IReadOnlyDictionary<string, string?> _lastOutputs = new Dictionary<string, string?>();

    /// <summary>
    /// Initializes a new instance of the <see cref="PulumiEnvironmentResource"/> class.
    /// </summary>
    /// <param name="name">The environment resource name. Used as the default Pulumi project name.</param>
    /// <param name="projectName">The Pulumi project name that groups stacks. Defaults to <paramref name="name"/>.</param>
    protected PulumiEnvironmentResource(string name, string? projectName = null)
        : base(name)
    {
        // The Aspire resource name is the Pulumi project by default. The Pulumi STACK is not baked in here:
        // it maps to the Aspire deployment environment (dev/staging/prod) and is resolved at deploy time from
        // IHostEnvironment, mirroring how Aspire itself partitions deployment state per environment
        // (see DistributedApplicationBuilder.LoadDeploymentState). Validate the project name up front so an
        // invalid value surfaces a clear error at AppHost build time rather than a cryptic Automation API failure.
        PulumiProjectName = PulumiNaming.ValidateName(projectName ?? name, nameof(projectName));

        Annotations.Add(new PipelineStepAnnotation(CreatePipelineStepsAsync));
        Annotations.Add(new PipelineConfigurationAnnotation(ConfigurePipeline));
    }

    /// <summary>Gets or sets the Pulumi project name that groups stacks in the Pulumi console.</summary>
    public string PulumiProjectName { get; set; }

    private string? _resolvedStackName;

    /// <summary>
    /// Gets or sets an explicit Pulumi stack name that overrides the deploy-time Aspire environment default.
    /// When <see langword="null"/> (the default), the stack is the Aspire environment name selected with
    /// <c>aspire deploy --environment &lt;name&gt;</c>. Set via <c>WithStackName</c>.
    /// </summary>
    public string? StackNameOverride { get; set; }

    /// <summary>
    /// Gets the Pulumi stack name, resolved at deploy time. When <see cref="StackNameOverride"/> is set it wins;
    /// otherwise the stack is the Aspire environment name (for example <c>dev</c>, <c>staging</c>, <c>prod</c>)
    /// selected with <c>aspire deploy --environment &lt;name&gt;</c> (precedence: <c>--environment</c> &gt;
    /// <c>DOTNET_ENVIRONMENT</c> &gt; <c>ASPIRE_ENVIRONMENT</c>), matching Pulumi's stack-per-environment model.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when accessed before the stack name is resolved at deploy time.</exception>
    public string StackName => _resolvedStackName
        ?? throw new InvalidOperationException("The Pulumi stack name is resolved at deploy time from the Aspire environment name.");

    /// <summary>
    /// Resolves and caches the Pulumi stack name. Uses <see cref="StackNameOverride"/> when set, otherwise the
    /// Aspire environment (<see cref="IHostEnvironment"/>). The environment name maps to the Pulumi stack, so
    /// <c>--environment prod</c> deploys the <c>prod</c> stack.
    /// </summary>
    /// <param name="services">The deploy-time service provider.</param>
    /// <returns>The validated, lower-cased stack name.</returns>
    internal string ResolveStackName(IServiceProvider services)
    {
        if (_resolvedStackName is not null)
        {
            return _resolvedStackName;
        }

        var (raw, paramName) = StackNameOverride is { } overridden
            ? (overridden, nameof(StackNameOverride))
            : (services.GetRequiredService<IHostEnvironment>().EnvironmentName.ToLowerInvariant(), "environment");

        return _resolvedStackName = PulumiNaming.ValidateName(raw, paramName);
    }

    /// <summary>Gets or sets the working directory for Pulumi operations. When unset, a per-project temp directory is used.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>Gets the container registry images are pushed to, or <see langword="null"/> when the environment has none.</summary>
    public virtual IContainerRegistry? ContainerRegistry => null;

    /// <summary>Gets the stack outputs captured by the most recent deploy, keyed by output name.</summary>
    public IReadOnlyDictionary<string, string?> LastOutputs => _lastOutputs;

    /// <summary>
    /// Builds the provider-specific cloud resources inside the Pulumi program. Called once per <c>pulumi up</c>.
    /// </summary>
    /// <param name="context">The publishing context for the running program.</param>
    public abstract Task CreateStackResourcesAsync(PulumiPublishingContext context);

    /// <inheritdoc />
    public abstract ReferenceExpression GetHostAddressExpression(EndpointReference endpointReference);

    /// <summary>
    /// Configures the Pulumi stack (for example provider configuration) before an operation runs.
    /// </summary>
    /// <param name="stack">The workspace stack.</param>
    /// <param name="context">The pipeline step context.</param>
    protected virtual Task ConfigureStackAsync(WorkspaceStack stack, PipelineStepContext context) => Task.CompletedTask;

    private async Task<IEnumerable<PipelineStep>> CreatePipelineStepsAsync(PipelineStepFactoryContext factoryContext)
    {
        var model = factoryContext.PipelineContext.Model;
        var steps = new List<PipelineStep>
        {
            new()
            {
                Name = PulumiPipelineSteps.PrepareDeploymentTargets(Name),
                Description = $"Prepares Pulumi deployment targets for {Name}.",
                Action = PrepareDeploymentTargetsAsync,
                DependsOnSteps = [WellKnownPipelineSteps.ValidateComputeEnvironments],
                RequiredBySteps = [WellKnownPipelineSteps.BeforeStart],
                Resource = this,
            },
            new()
            {
                Name = PulumiPipelineSteps.Publish(Name),
                Description = $"Writes a Pulumi preview artifact for {Name}.",
                Action = WritePublishArtifactAsync,
                DependsOnSteps = [WellKnownPipelineSteps.PublishPrereq],
                RequiredBySteps = [WellKnownPipelineSteps.Publish],
                Resource = this,
            },
            new()
            {
                Name = PulumiPipelineSteps.Deploy(Name),
                Description = $"Deploys {Name} using the Pulumi Automation API.",
                Action = DeployAsync,
                // Deploy after images are pushed so Container Apps reference real registry tags.
                DependsOnSteps = [WellKnownPipelineSteps.Push],
                RequiredBySteps = [WellKnownPipelineSteps.Deploy],
                Tags = [PulumiPipelineSteps.PulumiTag],
                Resource = this,
            },
            new()
            {
                Name = PulumiPipelineSteps.Destroy(Name),
                Description = $"Destroys all resources in {Name} using Pulumi.",
                Action = DestroyAsync,
                DependsOnSteps = [WellKnownPipelineSteps.DestroyPrereq],
                RequiredBySteps = [WellKnownPipelineSteps.Destroy],
                Resource = this,
            },
        };

        // Expand each deployment target's own steps (e.g. print-summary). The targets are not part of the
        // application model, so the environment resolves and inlines their PipelineStepAnnotation steps. This
        // relies on the prepare step having already run during the before-start phase.
        foreach (var (_, target) in EnumerateTargetResources(model))
        {
            if (target.TryGetAnnotationsOfType<PipelineStepAnnotation>(out var annotations))
            {
                foreach (var annotation in annotations)
                {
                    var childContext = new PipelineStepFactoryContext
                    {
                        PipelineContext = factoryContext.PipelineContext,
                        Resource = target,
                    };

                    var childSteps = await annotation.CreateStepsAsync(childContext).ConfigureAwait(false);
                    foreach (var step in childSteps)
                    {
                        step.Resource ??= target;
                    }

                    steps.AddRange(childSteps);
                }
            }
        }

        return steps;
    }

    private void ConfigurePipeline(PipelineConfigurationContext context)
    {
        var deployStepName = PulumiPipelineSteps.Deploy(Name);

        foreach (var (compute, target) in EnumerateTargetResources(context.Model))
        {
            // Pull the framework-created build steps into the deploy phase, after deploy-prereq runs. This
            // mirrors how the Kubernetes environment wires build steps so builds happen within deploy ordering.
            context.GetSteps(compute, WellKnownPipelineTags.BuildCompute)
                .RequiredBy(WellKnownPipelineSteps.Deploy)
                .DependsOn(WellKnownPipelineSteps.DeployPrereq);

            // Each target's print-summary step must run after the environment's deploy step.
            context.GetSteps(target, PulumiPipelineSteps.PrintSummaryTag).DependsOn(deployStepName);
        }
    }

    private Task PrepareDeploymentTargetsAsync(PipelineStepContext context)
    {
        // The environment is only added to the model in publish mode, but guard defensively.
        if (context.ExecutionContext.IsRunMode)
        {
            return Task.CompletedTask;
        }

        var publishingContext = new PulumiPublishingContext(
            context.Model,
            this,
            context.ExecutionContext,
            context.Services,
            context.Logger,
            context.CancellationToken);

        foreach (var compute in publishingContext.GetTargetedComputeResources())
        {
            var target = new PulumiDeploymentTargetResource($"{compute.Name}-pulumi", compute, this);

            compute.Annotations.Add(new DeploymentTargetAnnotation(target)
            {
                ComputeEnvironment = this,
                ContainerRegistry = ContainerRegistry,
            });
        }

        return Task.CompletedTask;
    }

    private async Task WritePublishArtifactAsync(PipelineStepContext context)
    {
        if (!context.ExecutionContext.IsPublishMode)
        {
            return;
        }

        var logger = context.Services.GetRequiredService<ILoggerFactory>().CreateLogger<PulumiEnvironmentResource>();
        var runner = context.Services.GetRequiredService<PulumiRunner>();

        // Resolve the stack (Aspire environment) before the program runs so physical resource names built in
        // CreateStackResourcesAsync read the same value.
        var stackName = ResolveStackName(context.Services);

        var task = await context.ReportingStep.CreateTaskAsync(
            $"Generating Pulumi preview for **{Name}**", context.CancellationToken).ConfigureAwait(false);

        await using (task.ConfigureAwait(false))
        {
            try
            {
                var result = await runner.ForStack(PulumiProjectName, stackName)
                    .WithWorkDir(WorkingDirectory)
                    .WithConfiguration((stack, _) => ConfigureStackAsync(stack, context))
                    .PreviewAsync(() => RunProgramAsync(context, logger), context.CancellationToken)
                    .ConfigureAwait(false);

                var outputDirectory = ResolveOutputDirectory(context);
                Directory.CreateDirectory(outputDirectory);
                var artifactPath = Path.Combine(outputDirectory, $"pulumi-{Name}-preview.txt");
                await File.WriteAllTextAsync(artifactPath, result.StandardOutput, context.CancellationToken).ConfigureAwait(false);

                await task.CompleteAsync(
                    $"Wrote Pulumi preview for **{Name}** to `{artifactPath}`.",
                    CompletionState.Completed,
                    context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await task.CompleteAsync(ex.Message, CompletionState.CompletedWithError, context.CancellationToken).ConfigureAwait(false);
                throw;
            }
        }
    }

    private async Task DeployAsync(PipelineStepContext context)
    {
        var logger = context.Services.GetRequiredService<ILoggerFactory>().CreateLogger<PulumiEnvironmentResource>();
        var runner = context.Services.GetRequiredService<PulumiRunner>();

        var stackName = ResolveStackName(context.Services);

        var task = await context.ReportingStep.CreateTaskAsync(
            $"Deploying **{Name}** with the Pulumi Automation API", context.CancellationToken).ConfigureAwait(false);

        await using (task.ConfigureAwait(false))
        {
            try
            {
                var result = await runner.ForStack(PulumiProjectName, stackName)
                    .WithWorkDir(WorkingDirectory)
                    .WithConfiguration((stack, _) => ConfigureStackAsync(stack, context))
                    .UpAsync(() => RunProgramAsync(context, logger), context.CancellationToken)
                    .ConfigureAwait(false);

                CaptureOutputs(context.Model, result.Outputs);

                await task.CompleteAsync(
                    $"Deployed **{Name}** successfully.",
                    CompletionState.Completed,
                    context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Fault output references so callers awaiting them observe the failure instead of hanging.
                FaultOutputs(context.Model, ex);
                await task.CompleteAsync(ex.Message, CompletionState.CompletedWithError, context.CancellationToken).ConfigureAwait(false);
                throw;
            }
        }
    }

    private async Task DestroyAsync(PipelineStepContext context)
    {
        var logger = context.Services.GetRequiredService<ILoggerFactory>().CreateLogger<PulumiEnvironmentResource>();
        var runner = context.Services.GetRequiredService<PulumiRunner>();

        var stackName = ResolveStackName(context.Services);

        var task = await context.ReportingStep.CreateTaskAsync(
            $"Destroying resources in **{Name}**", context.CancellationToken).ConfigureAwait(false);

        await using (task.ConfigureAwait(false))
        {
            try
            {
                await runner.ForStack(PulumiProjectName, StackName)
                    .WithWorkDir(WorkingDirectory)
                    .WithConfiguration((stack, _) => ConfigureStackAsync(stack, context))
                    .DestroyAsync(() => RunProgramAsync(context, logger), context.CancellationToken)
                    .ConfigureAwait(false);

                await task.CompleteAsync(
                    $"Destroyed resources in **{Name}**.",
                    CompletionState.Completed,
                    context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await task.CompleteAsync(ex.Message, CompletionState.CompletedWithError, context.CancellationToken).ConfigureAwait(false);
                throw;
            }
        }
    }

    private async Task<IDictionary<string, object?>> RunProgramAsync(PipelineStepContext context, ILogger logger)
    {
        var publishingContext = new PulumiPublishingContext(
            context.Model,
            this,
            context.ExecutionContext,
            context.Services,
            logger,
            context.CancellationToken);

        await CreateStackResourcesAsync(publishingContext).ConfigureAwait(false);
        return publishingContext.BuildOutputs();
    }

    private void CaptureOutputs(DistributedApplicationModel model, IReadOnlyDictionary<string, OutputValue> outputs)
    {
        var resolved = outputs.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Value?.ToString());
        _lastOutputs = resolved;

        foreach (var (_, target) in EnumerateTargetResources(model))
        {
            foreach (var reference in target.Outputs)
            {
                reference.SetValue(resolved.GetValueOrDefault(reference.Name));
            }
        }
    }

    private void FaultOutputs(DistributedApplicationModel model, Exception exception)
    {
        foreach (var (_, target) in EnumerateTargetResources(model))
        {
            foreach (var reference in target.Outputs)
            {
                reference.SetException(exception);
            }
        }
    }

    private IEnumerable<(IComputeResource compute, PulumiDeploymentTargetResource target)> EnumerateTargetResources(
        DistributedApplicationModel model)
    {
        foreach (var resource in model.GetComputeResources())
        {
            if (resource is not IComputeResource compute)
            {
                continue;
            }

            if (compute.GetDeploymentTargetAnnotation(this)?.DeploymentTarget is PulumiDeploymentTargetResource target)
            {
                yield return (compute, target);
            }
        }
    }

    private static string ResolveOutputDirectory(PipelineStepContext context)
    {
        var outputService = context.Services.GetService<IPipelineOutputService>();
        return outputService?.GetOutputDirectory() ?? Environment.CurrentDirectory;
    }
}
