// Licensed under the MIT License.

using System.Collections.Immutable;
using Pulumi.Automation;

namespace EmmittJ.Aspire.Hosting.Pulumi;

/// <summary>
/// Creates pre-configured <see cref="PulumiStackRunner"/> instances for executing Pulumi programs
/// through the Automation API. Registered as a singleton and injected into pipeline steps.
/// </summary>
public sealed class PulumiRunner
{
    /// <summary>
    /// Creates a runner targeting the specified Pulumi project and stack.
    /// </summary>
    /// <param name="projectName">The Pulumi project name (groups stacks in the Pulumi console).</param>
    /// <param name="stackName">The Pulumi stack name.</param>
    public PulumiStackRunner ForStack(string projectName, string stackName)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectName);
        ArgumentException.ThrowIfNullOrEmpty(stackName);
        return new PulumiStackRunner(projectName, stackName);
    }
}

/// <summary>
/// Executes Pulumi programs (up, preview, destroy) for a single project/stack through the Automation API.
/// </summary>
public sealed class PulumiStackRunner
{
    private readonly string _projectName;
    private readonly string _stackName;
    private string? _workDir;
    private Func<WorkspaceStack, CancellationToken, Task>? _configure;

    internal PulumiStackRunner(string projectName, string stackName)
    {
        _projectName = projectName;
        _stackName = stackName;
    }

    /// <summary>
    /// Sets an explicit working directory. When unset, a stable per-project temp directory is used.
    /// </summary>
    public PulumiStackRunner WithWorkDir(string? workDir)
    {
        if (!string.IsNullOrEmpty(workDir))
        {
            _workDir = workDir;
        }

        return this;
    }

    /// <summary>
    /// Sets a callback that configures the stack (for example provider config) before the operation runs.
    /// </summary>
    public PulumiStackRunner WithConfiguration(Func<WorkspaceStack, CancellationToken, Task> configure)
    {
        _configure = configure;
        return this;
    }

    /// <summary>
    /// Runs <c>pulumi up</c> for the inline program and returns the deployment result.
    /// </summary>
    /// <param name="program">The inline program that creates resources and returns stack outputs.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public async Task<PulumiUpResult> UpAsync(
        Func<Task<IDictionary<string, object?>>> program,
        CancellationToken cancellationToken = default)
    {
        var stack = await CreateStackAsync(program, cancellationToken).ConfigureAwait(false);

        // UpAsync throws CommandException on failure; let it propagate so callers report the real error
        // instead of inspecting a synthetic success flag.
        var result = await stack.UpAsync(new UpOptions(), cancellationToken).ConfigureAwait(false);

        return new PulumiUpResult(result.Outputs, result.Summary);
    }

    /// <summary>
    /// Runs <c>pulumi preview</c> (dry run) for the inline program and returns the change summary.
    /// </summary>
    public async Task<PulumiPreviewResult> PreviewAsync(
        Func<Task<IDictionary<string, object?>>> program,
        CancellationToken cancellationToken = default)
    {
        var stack = await CreateStackAsync(program, cancellationToken).ConfigureAwait(false);
        var result = await stack.PreviewAsync(new PreviewOptions(), cancellationToken).ConfigureAwait(false);

        return new PulumiPreviewResult(result.ChangeSummary, result.StandardOutput);
    }

    /// <summary>
    /// Runs <c>pulumi destroy</c> for the inline program.
    /// </summary>
    public async Task DestroyAsync(
        Func<Task<IDictionary<string, object?>>> program,
        CancellationToken cancellationToken = default)
    {
        var stack = await CreateStackAsync(program, cancellationToken).ConfigureAwait(false);
        await stack.DestroyAsync(new DestroyOptions(), cancellationToken).ConfigureAwait(false);
    }

    private async Task<WorkspaceStack> CreateStackAsync(
        Func<Task<IDictionary<string, object?>>> program,
        CancellationToken cancellationToken)
    {
        var stack = await LocalWorkspace.CreateOrSelectStackAsync(
            new InlineProgramArgs(_projectName, _stackName, PulumiFn.Create(program))
            {
                WorkDir = GetWorkDir()
            },
            cancellationToken).ConfigureAwait(false);

        if (_configure is not null)
        {
            await _configure(stack, cancellationToken).ConfigureAwait(false);
        }

        return stack;
    }

    private string GetWorkDir()
    {
        if (_workDir is not null)
        {
            return _workDir;
        }

        // Use a deterministic per-project temp directory rather than Directory.CreateTempSubdirectory():
        // the working directory must remain stable across up/preview/destroy for the same project so the
        // inline-program Pulumi.yaml/settings persist. State itself lives in the configured Pulumi backend,
        // not here. A dedicated directory also avoids picking up an ambient Pulumi.yaml from the AppHost cwd.
        var sanitized = string.Concat(_projectName.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-'));
        var path = Path.Combine(Path.GetTempPath(), "pulumi-aspire", sanitized);
        Directory.CreateDirectory(path);
        return path;
    }
}

/// <summary>Result of a <c>pulumi up</c> operation.</summary>
/// <param name="Outputs">The stack outputs.</param>
/// <param name="Summary">The update summary.</param>
public sealed record PulumiUpResult(
    IImmutableDictionary<string, OutputValue> Outputs,
    UpdateSummary Summary);

/// <summary>Result of a <c>pulumi preview</c> operation.</summary>
/// <param name="ChangeSummary">The per-operation change counts.</param>
/// <param name="StandardOutput">The human-readable preview output, suitable for a reviewable artifact.</param>
public sealed record PulumiPreviewResult(
    IImmutableDictionary<OperationType, int> ChangeSummary,
    string StandardOutput);
