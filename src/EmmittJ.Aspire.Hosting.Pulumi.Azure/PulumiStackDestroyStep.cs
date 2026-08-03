// Licensed under the MIT License.

using Aspire.Hosting;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EmmittJ.Aspire.Hosting.Pulumi.Azure;

/// <summary>
/// The <c>aspire destroy</c> counterpart of the Pulumi execution engine: a pipeline step that runs
/// <c>pulumi destroy</c> against the deployed stack so every Pulumi-managed resource is torn down through
/// Pulumi — keeping the stack's state consistent — before the native <c>destroy-azure-*</c> step deletes
/// the resource group itself via ARM.
/// </summary>
internal static class PulumiStackDestroyStep
{
    /// <summary>The name of the Pulumi destroy step.</summary>
    internal const string StepName = "destroy-pulumi-stack";

    private const string NativeAzureDestroyStepPrefix = "destroy-azure-";

    /// <summary>
    /// Creates the destroy step: it hangs off the standard destroy slots (<c>destroy-prereq</c> →
    /// <c>destroy</c>); <see cref="ConfigurePipelineAsync"/> additionally orders it before the native
    /// resource-group deletion.
    /// </summary>
    internal static PipelineStep CreateStep(PulumiProvisioningOptions options) => new()
    {
        Name = StepName,
        Description = "Destroys the Pulumi-managed Azure resources by running pulumi destroy against the stack.",
        Action = context => ExecuteAsync(options, context),
        DependsOnSteps = { WellKnownPipelineSteps.DestroyPrereq },
        RequiredBySteps = { WellKnownPipelineSteps.Destroy },
    };

    /// <summary>
    /// Orders the native <c>destroy-azure-*</c> resource-group deletion after the Pulumi destroy: the ARM
    /// deletion only starts (it does not wait for completion), so <c>pulumi destroy</c> must run first,
    /// while the resources still exist, to delete each resource through Pulumi and leave the stack's state
    /// empty instead of stale.
    /// </summary>
    internal static Task ConfigurePipelineAsync(PipelineConfigurationContext context)
    {
        foreach (var step in context.Steps)
        {
            if (step.Name.StartsWith(NativeAzureDestroyStepPrefix, StringComparison.Ordinal))
            {
                step.DependsOn(StepName);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The step action: resolves the same project/stack the deploy path targets, skips cleanly when the
    /// stack doesn't exist or is already empty, confirms with the user (mirroring the native destroy
    /// step's confirmation contract), and runs <c>pulumi destroy</c>.
    /// </summary>
    internal static async Task ExecuteAsync(PulumiProvisioningOptions options, PipelineStepContext context)
    {
        var hostEnvironment = context.Services.GetRequiredService<IHostEnvironment>();
        var projectName = PulumiStackNameResolver.ResolveProjectName(options, hostEnvironment);
        var stackName = PulumiStackNameResolver.ResolveStackName(options, hostEnvironment);
        var runner = context.Services.GetRequiredService<PulumiRunner>()
            .ForStack(projectName, stackName)
            .WithWorkDir(options.WorkingDirectory);

        var resourceCount = await runner.GetResourceCountAsync(context.CancellationToken).ConfigureAwait(false);
        if (resourceCount is null)
        {
            await context.ReportingStep.CompleteAsync(
                $"Pulumi stack '{projectName}/{stackName}' not found. Nothing to destroy.",
                CompletionState.Completed, context.CancellationToken).ConfigureAwait(false);
            return;
        }

        if (resourceCount == 0)
        {
            await context.ReportingStep.CompleteAsync(
                $"Pulumi stack '{projectName}/{stackName}' has no resources. Nothing to destroy.",
                CompletionState.Completed, context.CancellationToken).ConfigureAwait(false);
            return;
        }

        await ConfirmDestroyAsync(context, projectName, stackName, resourceCount.Value).ConfigureAwait(false);

        var destroyTask = await context.ReportingStep.CreateTaskAsync(
            $"Destroying {resourceCount} resource(s) in Pulumi stack '{projectName}/{stackName}'",
            context.CancellationToken).ConfigureAwait(false);
        await using (destroyTask.ConfigureAwait(false))
        {
            try
            {
                var result = await runner.DestroyAsync(context.CancellationToken).ConfigureAwait(false);

                context.Logger.LogInformation(
                    "Pulumi destroy for stack '{Project}/{Stack}' completed: {Result}.",
                    projectName, stackName, result?.Summary.Result);
                context.Summary.Add("\ud83d\uddd1\ufe0f Pulumi Stack", $"{projectName}/{stackName}");

                await destroyTask.CompleteAsync(
                    $"Destroyed {resourceCount} resource(s) in Pulumi stack '{projectName}/{stackName}'.",
                    CompletionState.Completed, context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await destroyTask.CompleteAsync(
                    $"pulumi destroy failed for stack '{projectName}/{stackName}': {ex.Message}",
                    CompletionState.CompletedWithError, context.CancellationToken).ConfigureAwait(false);
                throw;
            }
        }
    }

    /// <summary>Mirrors the native destroy step's confirmation contract: skipped with <c>--yes</c>, and a
    /// destructive operation is refused outright when no interaction channel is available.</summary>
    private static async Task ConfirmDestroyAsync(
        PipelineStepContext context, string projectName, string stackName, int resourceCount)
    {
        var pipelineOptions = context.Services.GetRequiredService<IOptions<PipelineOptions>>();
        if (pipelineOptions.Value.SkipConfirmation)
        {
            return;
        }

        var interactionService = context.Services.GetRequiredService<IInteractionService>();
        if (!interactionService.IsAvailable)
        {
            throw new InvalidOperationException(
                "Cannot perform destructive operation without confirmation. Use --yes to skip the confirmation prompt in non-interactive mode.");
        }

        var result = await interactionService.PromptNotificationAsync(
            "Destroy Pulumi-managed resources",
            $"Destroy {resourceCount} resource(s) in Pulumi stack '{projectName}/{stackName}'? This action cannot be undone.",
            new NotificationInteractionOptions
            {
                Intent = MessageIntent.Confirmation,
                ShowSecondaryButton = true,
                ShowDismiss = false,
                PrimaryButtonText = "Destroy",
                SecondaryButtonText = "Cancel",
            },
            context.CancellationToken).ConfigureAwait(false);

        if (result.Canceled || !result.Data)
        {
            context.Logger.LogInformation("User canceled the destroy operation.");
            throw new OperationCanceledException("Destroy operation canceled by user.");
        }
    }
}
