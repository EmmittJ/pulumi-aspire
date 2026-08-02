// Licensed under the MIT License.

#pragma warning disable ASPIREPIPELINES001 // Pipeline APIs are experimental
#pragma warning disable ASPIREPIPELINES004 // IPipelineOutputService is experimental
#pragma warning disable ASPIRECOMPUTE002  // IComputeEnvironmentResource is experimental

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EmmittJ.Aspire.Hosting.Pulumi;

/// <summary>
/// The Pulumi environment for an adopted native Aspire compute environment. Splices Pulumi-owned
/// publish/deploy/destroy steps into the standard pipeline slots and runs the user-supplied program over
/// the provisioning model the native environment's modeling steps materialized.
/// </summary>
/// <remarks>
/// <para>
/// This is the execution half of the adopt-and-traverse model (see
/// <c>docs/spikes/native-environment-step-adoption.md</c>): the adopted native environment
/// (<c>AddAzureContainerAppEnvironment</c>, <c>AddAzureAppServiceEnvironment</c>) keeps
/// materializing the full provisioning model through its
/// prepare/publish steps, while its execution steps are suppressed by
/// <see cref="NativePipelineStepAdoption.SuppressExecutionSteps"/> and this resource's spliced steps become
/// the execution engine. Create it with the <c>PublishAsPulumi</c> decorator rather than directly.
/// </para>
/// <para>
/// The deploy step deliberately depends on both <c>push</c> and <c>before-start</c>: the pipeline scheduler
/// runs independent steps concurrently, and without the <c>before-start</c> edge the deploy step can start
/// while a native prepare step (which attaches <see cref="DeploymentTargetAnnotation"/>s) is still running,
/// observing a half-materialized model.
/// </para>
/// </remarks>
public sealed class PulumiEnvironmentResource : Resource
{
    private readonly Func<PulumiPublishingContext, Task> _program;
    private string? _resolvedStackName;
    private IReadOnlyDictionary<string, string?> _lastOutputs = new Dictionary<string, string?>();

    /// <summary>
    /// Initializes a new instance of the <see cref="PulumiEnvironmentResource"/> class.
    /// </summary>
    /// <param name="name">The environment resource name. Used as the default Pulumi project name.</param>
    /// <param name="adoptedEnvironment">The native compute environment this resource adopts.</param>
    /// <param name="program">
    /// The Pulumi program run for publish previews, deploys, and destroys. It receives a
    /// <see cref="PulumiPublishingContext"/> to walk the materialized deployment targets and create the
    /// corresponding Pulumi resources.
    /// </param>
    /// <param name="projectName">The Pulumi project name that groups stacks. Defaults to <paramref name="name"/>.</param>
    public PulumiEnvironmentResource(
        string name,
        IComputeEnvironmentResource adoptedEnvironment,
        Func<PulumiPublishingContext, Task> program,
        string? projectName = null)
        : base(name)
    {
        ArgumentNullException.ThrowIfNull(adoptedEnvironment);
        ArgumentNullException.ThrowIfNull(program);

        AdoptedEnvironment = adoptedEnvironment;
        _program = program;
        PulumiProjectName = PulumiNaming.ValidateName(projectName ?? name, nameof(projectName));

        Annotations.Add(new PipelineStepAnnotation(CreatePipelineSteps));
    }

    /// <summary>Gets the adopted native compute environment resource.</summary>
    public IComputeEnvironmentResource AdoptedEnvironment { get; }

    /// <summary>Gets or sets the Pulumi project name that groups stacks in the Pulumi console.</summary>
    public string PulumiProjectName { get; set; }

    /// <summary>
    /// Gets or sets the registry-first Pulumi phase. When set, the environment splices a
    /// <c>pulumi-deploy-registry-{name}</c> step (required by <c>push-prereq</c>) that provisions the
    /// adopted environment's container registry into the dedicated <c>{project}-registry</c> stack and
    /// authenticates Docker to it before Aspire's push step runs — replacing the suppressed native registry
    /// login step (for example Azure Container Apps' <c>login-to-acr-*</c>). A matching
    /// <c>pulumi-destroy-registry-{name}</c> step tears the registry stack down after the main stack.
    /// </summary>
    public PulumiRegistryPhase? RegistryPhase { get; set; }

    /// <summary>Gets the Pulumi project name of the registry-first phase's dedicated stack.</summary>
    public string RegistryProjectName => $"{PulumiProjectName}-registry";

    /// <summary>
    /// Gets or sets an explicit Pulumi stack name that overrides the deploy-time Aspire environment default.
    /// When <see langword="null"/> (the default), the stack is the Aspire environment name selected with
    /// <c>aspire deploy --environment &lt;name&gt;</c>.
    /// </summary>
    public string? StackNameOverride { get; set; }

    /// <summary>Gets or sets the working directory for Pulumi operations. When unset, a per-project temp directory is used.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>Gets the stack outputs captured by the most recent deploy, keyed by output name.</summary>
    public IReadOnlyDictionary<string, string?> LastOutputs => _lastOutputs;

    /// <summary>
    /// Resolves and caches the Pulumi stack name. Uses <see cref="StackNameOverride"/> when set, otherwise
    /// the Aspire environment (<see cref="IHostEnvironment"/>).
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

    private Task<IEnumerable<PipelineStep>> CreatePipelineSteps(PipelineStepFactoryContext factoryContext)
    {
        var steps = new List<PipelineStep>
        {
            new()
            {
                Name = PulumiPipelineSteps.Publish(Name),
                Description = $"Writes a Pulumi preview artifact for {Name} (adopting {AdoptedEnvironment.Name}).",
                Action = WritePublishArtifactAsync,
                DependsOnSteps = [WellKnownPipelineSteps.PublishPrereq],
                RequiredBySteps = [WellKnownPipelineSteps.Publish],
                Resource = this,
            },
            new()
            {
                Name = PulumiPipelineSteps.Deploy(Name),
                Description = $"Deploys {Name} (adopting {AdoptedEnvironment.Name}) using the Pulumi Automation API.",
                Action = DeployAsync,
                // Deploy after images are pushed so workloads reference real registry tags. The before-start
                // edge is deliberate: without it the scheduler can start this step while a native prepare
                // step (which attaches DeploymentTargetAnnotations) is still running.
                DependsOnSteps = [WellKnownPipelineSteps.Push, WellKnownPipelineSteps.BeforeStart],
                RequiredBySteps = [WellKnownPipelineSteps.Deploy],
                Tags = [PulumiPipelineSteps.PulumiTag],
                Resource = this,
            },
            new()
            {
                Name = PulumiPipelineSteps.Destroy(Name),
                Description = $"Destroys all resources in {Name} (adopting {AdoptedEnvironment.Name}) using Pulumi.",
                Action = DestroyAsync,
                DependsOnSteps = [WellKnownPipelineSteps.DestroyPrereq],
                RequiredBySteps = [WellKnownPipelineSteps.Destroy],
                Resource = this,
            },
        };

        if (RegistryPhase is not null)
        {
            steps.Add(new PipelineStep
            {
                Name = PulumiPipelineSteps.DeployRegistry(Name),
                Description = $"Provisions the container registry for {Name} (adopting {AdoptedEnvironment.Name}) using Pulumi and authenticates to it.",
                Action = DeployRegistryAsync,
                Tags = [WellKnownPipelineTags.ProvisionInfrastructure, PulumiPipelineSteps.PulumiTag],
                // The registry must exist (and Docker be logged in) before Aspire's push step runs. The
                // before-start edge is deliberate: the native prepare steps that attach the registry to the
                // DeploymentTargetAnnotations run concurrently otherwise.
                DependsOnSteps = [WellKnownPipelineSteps.BeforeStart],
                RequiredBySteps = [WellKnownPipelineSteps.PushPrereq],
                Resource = this,
            });

            steps.Add(new PipelineStep
            {
                Name = PulumiPipelineSteps.DestroyRegistry(Name),
                Description = $"Destroys the container registry stack for {Name} using Pulumi.",
                Action = DestroyRegistryAsync,
                // The registry is its own Pulumi stack, so it needs its own destroy step; it runs after the
                // main destroy so workloads referencing registry images are gone before the registry is.
                DependsOnSteps = [WellKnownPipelineSteps.DestroyPrereq, PulumiPipelineSteps.Destroy(Name)],
                RequiredBySteps = [WellKnownPipelineSteps.Destroy],
                Resource = this,
            });
        }

        return Task.FromResult<IEnumerable<PipelineStep>>(steps);
    }

    private async Task WritePublishArtifactAsync(PipelineStepContext context)
    {
        if (!context.ExecutionContext.IsPublishMode)
        {
            return;
        }

        await RunStepAsync(
            context,
            PulumiProjectName,
            $"Generating Pulumi preview for **{Name}**",
            async (stack, logger) =>
            {
                var result = await stack
                    .PreviewAsync(() => RunProgramAsync(context, PulumiOperation.Preview, logger), context.CancellationToken)
                    .ConfigureAwait(false);

                var outputDirectory = ResolveOutputDirectory(context);
                Directory.CreateDirectory(outputDirectory);
                var artifactPath = Path.Combine(outputDirectory, $"pulumi-{Name}-preview.txt");
                await File.WriteAllTextAsync(artifactPath, result.StandardOutput, context.CancellationToken).ConfigureAwait(false);

                return $"Wrote Pulumi preview for **{Name}** to `{artifactPath}`.";
            }).ConfigureAwait(false);
    }

    private Task DeployAsync(PipelineStepContext context) =>
        RunStepAsync(
            context,
            PulumiProjectName,
            $"Deploying **{Name}** with the Pulumi Automation API",
            async (stack, logger) =>
            {
                var result = await stack
                    .UpAsync(() => RunProgramAsync(context, PulumiOperation.Up, logger), context.CancellationToken)
                    .ConfigureAwait(false);

                _lastOutputs = result.Outputs.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Value?.ToString());

                return $"Deployed **{Name}** successfully.";
            });

    private Task DestroyAsync(PipelineStepContext context) =>
        RunStepAsync(
            context,
            PulumiProjectName,
            $"Destroying resources in **{Name}**",
            async (stack, logger) =>
            {
                await stack
                    .DestroyAsync(() => RunProgramAsync(context, PulumiOperation.Destroy, logger), context.CancellationToken)
                    .ConfigureAwait(false);

                return $"Destroyed resources in **{Name}**.";
            });

    private Task DeployRegistryAsync(PipelineStepContext context)
    {
        if (RegistryPhase is not { } phase)
        {
            return Task.CompletedTask;
        }

        return RunStepAsync(
            context,
            RegistryProjectName,
            $"Provisioning the container registry for **{Name}** with Pulumi",
            async (stack, logger) =>
            {
                await stack
                    .UpAsync(() => RunProgramAsync(context, PulumiOperation.Up, logger, phase.Program), context.CancellationToken)
                    .ConfigureAwait(false);

                if (phase.LoginCallback is { } login)
                {
                    var publishingContext = CreatePublishingContext(context, PulumiOperation.Up, logger);
                    foreach (var registry in publishingContext.GetContainerRegistries())
                    {
                        await login(context, registry).ConfigureAwait(false);
                    }
                }

                return $"Container registry for **{Name}** provisioned.";
            });
    }

    private Task DestroyRegistryAsync(PipelineStepContext context)
    {
        if (RegistryPhase is not { } phase)
        {
            return Task.CompletedTask;
        }

        return RunStepAsync(
            context,
            RegistryProjectName,
            $"Destroying the container registry stack for **{Name}**",
            async (stack, logger) =>
            {
                await stack
                    .DestroyAsync(() => RunProgramAsync(context, PulumiOperation.Destroy, logger, phase.Program), context.CancellationToken)
                    .ConfigureAwait(false);

                return $"Container registry stack for **{Name}** destroyed.";
            });
    }

    /// <summary>
    /// Shared step scaffolding for every spliced Pulumi step: resolves the runner and stack name, wraps the
    /// operation in a reporting task, and completes it with the operation's success message (or the error).
    /// </summary>
    private async Task RunStepAsync(
        PipelineStepContext context,
        string projectName,
        string taskTitle,
        Func<PulumiStackRunner, ILogger, Task<string>> operation)
    {
        var logger = context.Services.GetRequiredService<ILoggerFactory>().CreateLogger<PulumiEnvironmentResource>();
        var runner = context.Services.GetRequiredService<PulumiRunner>();
        var stackName = ResolveStackName(context.Services);

        var task = await context.ReportingStep.CreateTaskAsync(taskTitle, context.CancellationToken).ConfigureAwait(false);

        await using (task.ConfigureAwait(false))
        {
            try
            {
                var stack = runner.ForStack(projectName, stackName).WithWorkDir(WorkingDirectory);
                var successMessage = await operation(stack, logger).ConfigureAwait(false);

                await task.CompleteAsync(successMessage, CompletionState.Completed, context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await task.CompleteAsync(ex.Message, CompletionState.CompletedWithError, context.CancellationToken).ConfigureAwait(false);
                throw;
            }
        }
    }

    private async Task<IDictionary<string, object?>> RunProgramAsync(
        PipelineStepContext context,
        PulumiOperation operation,
        ILogger logger,
        Func<PulumiPublishingContext, Task>? program = null)
    {
        var publishingContext = CreatePublishingContext(context, operation, logger);
        await (program ?? _program)(publishingContext).ConfigureAwait(false);
        return publishingContext.BuildOutputs();
    }

    private PulumiPublishingContext CreatePublishingContext(PipelineStepContext context, PulumiOperation operation, ILogger logger) =>
        new(
            context.Model,
            this,
            operation,
            context.ExecutionContext,
            context.Services,
            logger,
            context.CancellationToken);

    private static string ResolveOutputDirectory(PipelineStepContext context)
    {
        var outputService = context.Services.GetService<IPipelineOutputService>();
        return outputService?.GetOutputDirectory() ?? Environment.CurrentDirectory;
    }
}
