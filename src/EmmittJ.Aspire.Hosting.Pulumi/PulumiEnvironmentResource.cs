// Licensed under the Apache License, Version 2.0.

#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pulumi.Automation;
using Pulumi.Automation.Events;

namespace EmmittJ.Aspire.Hosting.Pulumi;

/// <summary>
/// Base class for Pulumi-managed compute environment resources.
/// Provides common functionality for deploying Aspire applications with Pulumi using the Automation API.
/// </summary>
/// <remarks>
/// <para>
/// This base class handles all Automation API orchestration including stack creation,
/// deployment, preview, and destroy operations. Provider packages only need to implement
/// <see cref="CreateResourcesAsync"/> to create provider-specific cloud resources.
/// </para>
/// <para>
/// The resource name is used as the Pulumi stack name.
/// </para>
/// </remarks>
public abstract class PulumiEnvironmentResource : Resource, IPulumiEnvironmentResource
{
    private WorkspaceStack? _stack;
    private IDictionary<string, OutputValue>? _lastOutputs;

    /// <summary>
    /// Initializes a new instance of the <see cref="PulumiEnvironmentResource"/> class.
    /// </summary>
    /// <param name="name">The name of the resource (and Pulumi stack).</param>
    protected PulumiEnvironmentResource(string name) : base(name)
    {
        // Default naming: use resource name for both project and environment
        PulumiProjectName = name;
        EnvironmentName = name;

        // Register pipeline steps via annotation
        Annotations.Add(new PipelineStepAnnotation(CreatePipelineStepsAsync));
    }

    /// <summary>
    /// Gets or sets the Pulumi project name.
    /// All stacks are grouped under this project in the Pulumi console.
    /// </summary>
    public string PulumiProjectName { get; set; }

    /// <summary>
    /// Gets or sets the environment name used in stack naming.
    /// </summary>
    public string EnvironmentName { get; set; }

    /// <summary>
    /// Gets the Pulumi stack name for the container registry.
    /// </summary>
    /// <value>Returns <c>{PulumiProjectName}-{EnvironmentName}-registry</c>.</value>
    public string RegistryStackName => $"{PulumiProjectName}-{EnvironmentName}-registry";

    /// <summary>
    /// Gets the Pulumi stack name for the main environment.
    /// </summary>
    /// <value>Returns <c>{PulumiProjectName}-{EnvironmentName}</c>.</value>
    public string EnvironmentStackName => $"{PulumiProjectName}-{EnvironmentName}";

    /// <summary>
    /// Gets a unique prefix for Azure resource naming based on project and environment.
    /// </summary>
    /// <value>Returns <c>{PulumiProjectName}-{EnvironmentName}</c>.</value>
    public string ResourcePrefix => $"{PulumiProjectName}-{EnvironmentName}";

    /// <summary>
    /// Gets a deterministic seed value based on project and environment names.
    /// </summary>
    public int DeterministicSeed => HashCode.Combine(PulumiProjectName, EnvironmentName);

    /// <summary>
    /// Gets or sets the working directory for Pulumi operations.
    /// If not set, uses the current directory.
    /// </summary>
    public string? WorkDir { get; set; }

    /// <summary>
    /// Gets the last deployment outputs.
    /// Available after a successful deployment.
    /// </summary>
    public IDictionary<string, OutputValue>? LastOutputs => _lastOutputs;

    /// <inheritdoc />
    public abstract ReferenceExpression GetHostAddressExpression(EndpointReference endpointReference);

    /// <inheritdoc />
    public abstract Task CreateResourcesAsync(PulumiPublishingContext context);

    /// <summary>
    /// Creates the pipeline steps for this environment.
    /// </summary>
    protected virtual async Task<IEnumerable<PipelineStep>> CreatePipelineStepsAsync(PipelineStepFactoryContext factoryContext)
    {
        var steps = new List<PipelineStep>();

        // Main deploy step
        var deployStep = new PipelineStep
        {
            Name = PulumiWellKnownPipelineSteps.Deploy(Name),
            Description = $"Deploys resources to {Name} using Pulumi.",
            Action = DeployAsync,
            Resource = this,
            Tags = [PulumiWellKnownPipelineSteps.PulumiTag]
        };
        deployStep.RequiredBy(WellKnownPipelineSteps.Deploy);
        // Deploy depends on Push (which includes building and pushing images)
        deployStep.DependsOn(WellKnownPipelineSteps.Push);
        steps.Add(deployStep);

        // Preview step
        var previewStep = new PipelineStep
        {
            Name = PulumiWellKnownPipelineSteps.Preview(Name),
            Description = $"Previews changes for {Name} using Pulumi (dry run).",
            Action = PreviewAsync,
            Resource = this,
            Tags = [PulumiWellKnownPipelineSteps.PreviewTag]
        };
        previewStep.DependsOn(WellKnownPipelineSteps.PublishPrereq);
        steps.Add(previewStep);

        // Destroy step
        var destroyStep = new PipelineStep
        {
            Name = PulumiWellKnownPipelineSteps.Destroy(Name),
            Description = $"Destroys all resources in {Name} using Pulumi.",
            Action = DestroyAsync,
            Resource = this,
            Tags = [PulumiWellKnownPipelineSteps.DestroyTag]
        };
        steps.Add(destroyStep);

        // Print summary step
        var printSummaryStep = new PipelineStep
        {
            Name = PulumiWellKnownPipelineSteps.PrintSummary(Name),
            Description = $"Prints deployment summary for {Name}.",
            Action = PrintSummaryAsync,
            Resource = this,
            Tags = [PulumiWellKnownPipelineSteps.PrintSummaryTag],
            DependsOnSteps = [PulumiWellKnownPipelineSteps.Deploy(Name)],
            RequiredBySteps = [WellKnownPipelineSteps.Deploy]
        };
        steps.Add(printSummaryStep);

        await Task.CompletedTask;
        return steps;
    }

    /// <summary>
    /// Gets or creates the Pulumi stack using the Automation API.
    /// </summary>
    /// <param name="context">The pipeline step context.</param>
    /// <returns>The workspace stack.</returns>
    protected virtual async Task<WorkspaceStack> GetOrCreateStackAsync(PipelineStepContext context)
    {
        if (_stack is not null)
        {
            return _stack;
        }

        var logger = context.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger<PulumiEnvironmentResource>();

        var projectName = PulumiProjectName;
        var stackName = EnvironmentStackName;
        var workDir = WorkDir ?? Environment.CurrentDirectory;

        logger.LogDebug(
            "Creating Pulumi stack '{StackName}' for project '{ProjectName}' in '{WorkDir}'",
            stackName, projectName, workDir);

        // Create the inline program that will create resources
        var program = PulumiFn.Create(async () =>
        {
            var publishingContext = new PulumiPublishingContext(
                context.Model,
                this,
                context,
                context.ExecutionContext,
                logger);

            // Let the provider-specific implementation create resources
            await CreateResourcesAsync(publishingContext);

            // Return outputs
            return publishingContext.BuildOutputs();
        });

        // Create or select the stack
        try
        {
            _stack = await LocalWorkspace.CreateOrSelectStackAsync(
                new InlineProgramArgs(projectName, stackName, program)
                {
                    WorkDir = workDir
                },
                context.CancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create or select Pulumi stack '{StackName}'", stackName);
            throw;
        }

        // Apply any stack configuration
        await ConfigureStackAsync(_stack, context, logger);

        logger.LogInformation(
            "Created Pulumi stack '{StackName}' for project '{ProjectName}'",
            stackName, projectName);

        return _stack;
    }

    /// <summary>
    /// Configures the stack with any required settings.
    /// Override in derived classes to add provider-specific configuration.
    /// </summary>
    /// <param name="stack">The workspace stack.</param>
    /// <param name="context">The pipeline step context.</param>
    /// <param name="logger">The logger instance.</param>
    protected virtual Task ConfigureStackAsync(
        WorkspaceStack stack,
        PipelineStepContext context,
        ILogger logger)
    {
        // Base implementation does nothing
        // Provider packages can override to set provider-specific config
        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates the resource creation program that can be used with either the engine or Automation API.
    /// </summary>
    /// <param name="context">The pipeline step context.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>A function that creates resources and returns outputs.</returns>
    protected Func<Task<IDictionary<string, object?>>> CreateProgram(PipelineStepContext context, ILogger logger)
    {
        return async () =>
        {
            var publishingContext = new PulumiPublishingContext(
                context.Model,
                this,
                context,
                context.ExecutionContext,
                logger);

            // Let the provider-specific implementation create resources
            await CreateResourcesAsync(publishingContext);

            // Return outputs
            return publishingContext.BuildOutputs();
        };
    }

    /// <summary>
    /// Executes the deploy step. Uses PulumiRunner which automatically chooses the right
    /// execution mode based on whether we're under the Pulumi engine or running standalone.
    /// </summary>
    protected virtual async Task DeployAsync(PipelineStepContext context)
    {
        var logger = context.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger<PulumiEnvironmentResource>();

        var runner = context.Services.GetRequiredService<PulumiRunner>();
        var modeLabel = runner.EngineContext.IsRunningUnderEngine ? "engine mode" : "Automation API";

        var task = await context.ReportingStep.CreateTaskAsync(
            $"Deploying to **{Name}** with Pulumi ({modeLabel})", context.CancellationToken);

        await using (task)
        {
            try
            {
                logger.LogInformation(
                    "Running deployment for '{Name}' via {Mode}...",
                    Name, modeLabel);

                var program = CreateProgram(context, logger);

                var result = await runner.ForStack(PulumiProjectName, EnvironmentStackName)
                    .WithMode(PulumiRunnerMode.Auto)
                    .WithWorkDir(WorkDir)
                    .WithConfiguration((stack, ct) => ConfigureStackAsync(stack, context, logger))
                    .RunAsync(program, context.CancellationToken);

                if (!result.Success)
                {
                    throw new InvalidOperationException(result.ErrorMessage ?? "Deployment failed");
                }

                if (result.Outputs is not null)
                {
                    _lastOutputs = result.Outputs.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                }

                logger.LogInformation(
                    "Deployment to '{Name}' completed successfully via {Mode}.",
                    Name, modeLabel);

                await task.CompleteAsync(
                    $"Deployment to **{Name}** completed successfully ({modeLabel}).",
                    CompletionState.Completed,
                    context.CancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Deployment to '{Name}' failed", Name);
                await task.FailAsync(ex.Message, cancellationToken: context.CancellationToken);
                throw;
            }
        }
    }

    /// <summary>
    /// Executes the preview step.
    /// </summary>
    protected virtual async Task PreviewAsync(PipelineStepContext context)
    {
        var logger = context.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger<PulumiEnvironmentResource>();

        var runner = context.Services.GetRequiredService<PulumiRunner>();
        var modeLabel = runner.EngineContext.IsRunningUnderEngine ? "engine mode" : "Automation API";

        var task = await context.ReportingStep.CreateTaskAsync(
            $"Previewing changes for **{Name}** ({modeLabel})", context.CancellationToken);

        await using (task)
        {
            try
            {
                logger.LogInformation(
                    "Running preview for '{Name}' via {Mode}...",
                    Name, modeLabel);

                var program = CreateProgram(context, logger);

                var result = await runner.ForStack(PulumiProjectName, EnvironmentStackName)
                    .WithMode(PulumiRunnerMode.Auto)
                    .WithWorkDir(WorkDir)
                    .WithConfiguration((stack, ct) => ConfigureStackAsync(stack, context, logger))
                    .PreviewAsync(program, context.CancellationToken);

                if (!result.Success)
                {
                    throw new InvalidOperationException(result.ErrorMessage ?? "Preview failed");
                }

                var changeCount = result.ChangeSummary?.Count ?? 0;

                logger.LogInformation(
                    "Preview for '{Name}' completed. {ChangeCount} changes detected.",
                    Name, changeCount);

                await task.CompleteAsync(
                    $"Preview for **{Name}** completed. {changeCount} changes detected.",
                    CompletionState.Completed,
                    context.CancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Preview for '{Name}' failed", Name);
                await task.FailAsync(ex.Message, cancellationToken: context.CancellationToken);
                throw;
            }
        }
    }

    /// <summary>
    /// Executes the destroy step using the Automation API.
    /// </summary>
    protected virtual async Task DestroyAsync(PipelineStepContext context)
    {
        var logger = context.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger<PulumiEnvironmentResource>();

        var task = await context.ReportingStep.CreateTaskAsync(
            $"Destroying resources in **{Name}**", context.CancellationToken);

        await using (task)
        {
            try
            {
                var stack = await GetOrCreateStackAsync(context);

                logger.LogInformation("Running 'pulumi destroy' for stack '{StackName}'...", Name);

                var result = await stack.DestroyAsync(new DestroyOptions
                {
                    OnStandardOutput = msg => logger.LogInformation("{Message}", msg),
                    OnStandardError = msg => logger.LogError("{Message}", msg),
                    OnEvent = evt => HandlePulumiEvent(evt, logger)
                }, context.CancellationToken);

                logger.LogInformation(
                    "Destroy for stack '{StackName}' completed. Summary: {Summary}",
                    Name,
                    result.Summary.Message);

                await task.CompleteAsync(
                    $"Resources in **{Name}** destroyed successfully.",
                    CompletionState.Completed,
                    context.CancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Destroy for stack '{StackName}' failed", Name);
                await task.FailAsync(ex.Message, cancellationToken: context.CancellationToken);
                throw;
            }
        }
    }

    /// <summary>
    /// Prints a summary of the deployment outputs.
    /// </summary>
    protected virtual async Task PrintSummaryAsync(PipelineStepContext context)
    {
        var logger = context.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger<PulumiEnvironmentResource>();

        if (_stack is null)
        {
            logger.LogWarning("No stack available for print summary");
            return;
        }

        try
        {
            var outputs = await _stack.GetOutputsAsync(context.CancellationToken);

            if (outputs.Count == 0)
            {
                logger.LogInformation("No outputs available for stack '{StackName}'", Name);
                return;
            }

            logger.LogInformation("Stack '{StackName}' outputs:", Name);
            foreach (var (key, value) in outputs)
            {
                var displayValue = value.IsSecret ? "***" : value.Value?.ToString() ?? "(null)";
                logger.LogInformation("  {Key}: {Value}", key, displayValue);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get outputs for stack '{StackName}'", Name);
        }
    }

    /// <summary>
    /// Handles Pulumi engine events during operations.
    /// </summary>
    /// <param name="engineEvent">The engine event.</param>
    /// <param name="logger">The logger instance.</param>
    protected virtual void HandlePulumiEvent(EngineEvent engineEvent, ILogger logger)
    {
        // Log resource progress events
        if (engineEvent.ResourcePreEvent is { } preEvent)
        {
            logger.LogDebug(
                "  {Operation} {Type} {Name}...",
                preEvent.Metadata.Op,
                preEvent.Metadata.Type,
                preEvent.Metadata.Urn);
        }

        if (engineEvent.ResourceOutputsEvent is { } outputEvent)
        {
            logger.LogDebug(
                "  {Type} {Name} complete",
                outputEvent.Metadata.Type,
                outputEvent.Metadata.Urn);
        }

        if (engineEvent.DiagnosticEvent is { } diagEvent)
        {
            var severity = diagEvent.Severity;
            var message = diagEvent.Message;

            switch (severity)
            {
                case "error":
                    logger.LogError("{Message}", message);
                    break;
                case "warning":
                    logger.LogWarning("{Message}", message);
                    break;
                case "info":
                    logger.LogInformation("{Message}", message);
                    break;
                default:
                    logger.LogDebug("{Message}", message);
                    break;
            }
        }
    }
}
