// Licensed under the Apache License, Version 2.0.

using System.Collections.Immutable;
using Pulumi.Automation;

namespace EmmittJ.Aspire.Hosting.Pulumi;

/// <summary>
/// Factory for creating pre-configured <see cref="PulumiStackRunner"/> instances.
/// </summary>
/// <remarks>
/// <para>
/// Inject this class via DI to create runners for specific projects/stacks.
/// This avoids repeating project name, stack name, and configuration on every call.
/// </para>
/// <example>
/// <code>
/// var result = await pulumiRunner.ForStack("my-project", "dev")
///     .WithConfiguration(ConfigureStackAsync)
///     .RunAsync(program, cancellationToken);
/// </code>
/// </example>
/// </remarks>
public sealed class PulumiRunner
{
    /// <summary>
    /// Creates a runner configured for the specified project and stack.
    /// </summary>
    /// <param name="projectName">The Pulumi project name.</param>
    /// <param name="stackName">The Pulumi stack name.</param>
    /// <returns>A builder for further configuration.</returns>
    public PulumiStackRunnerBuilder ForStack(string projectName, string stackName)
    {
        return new PulumiStackRunnerBuilder(projectName, stackName);
    }
}

/// <summary>
/// Builder for creating a configured <see cref="PulumiStackRunner"/>.
/// </summary>
public sealed class PulumiStackRunnerBuilder
{
    private readonly string _projectName;
    private readonly string _stackName;
    private string? _explicitWorkDir;
    private Func<WorkspaceStack, CancellationToken, Task>? _configureStack;

    internal PulumiStackRunnerBuilder(string projectName, string stackName)
    {
        _projectName = projectName;
        _stackName = stackName;
    }

    /// <summary>
    /// Sets an explicit working directory for Pulumi operations.
    /// If not set (or null), a temp directory is used.
    /// </summary>
    public PulumiStackRunnerBuilder WithWorkDir(string? workDir)
    {
        if (!string.IsNullOrEmpty(workDir))
        {
            _explicitWorkDir = workDir;
        }
        return this;
    }

    /// <summary>
    /// Sets a callback to configure the stack before operations.
    /// </summary>
    public PulumiStackRunnerBuilder WithConfiguration(Func<WorkspaceStack, CancellationToken, Task> configureStack)
    {
        _configureStack = configureStack;
        return this;
    }

    /// <summary>
    /// Builds the configured stack runner.
    /// </summary>
    public PulumiStackRunner Build()
    {
        return new PulumiStackRunner(
            _projectName,
            _stackName,
            _explicitWorkDir,
            _configureStack);
    }

    /// <summary>
    /// Executes the Pulumi program (up/deploy).
    /// </summary>
    public Task<PulumiRunResult> RunAsync(
        Func<Task<IDictionary<string, object?>>> program,
        CancellationToken cancellationToken = default)
        => Build().RunAsync(program, cancellationToken);

    /// <summary>
    /// Executes a Pulumi preview (dry run).
    /// </summary>
    public Task<PulumiRunResult> PreviewAsync(
        Func<Task<IDictionary<string, object?>>> program,
        CancellationToken cancellationToken = default)
        => Build().PreviewAsync(program, cancellationToken);
}

/// <summary>
/// A pre-configured runner for a specific Pulumi project/stack.
/// </summary>
public sealed class PulumiStackRunner
{
    private readonly string _projectName;
    private readonly string _stackName;
    private readonly string? _explicitWorkDir;
    private readonly Func<WorkspaceStack, CancellationToken, Task>? _configureStack;

    internal PulumiStackRunner(
        string projectName,
        string stackName,
        string? explicitWorkDir,
        Func<WorkspaceStack, CancellationToken, Task>? configureStack)
    {
        _projectName = projectName;
        _stackName = stackName;
        _explicitWorkDir = explicitWorkDir;
        _configureStack = configureStack;
    }

    /// <summary>
    /// Gets the working directory for Automation API operations.
    /// Uses explicit work dir if set, otherwise creates a temp directory.
    /// </summary>
    private string GetWorkDir()
    {
        if (_explicitWorkDir is not null)
        {
            return _explicitWorkDir;
        }

        // Default to temp directory to avoid picking up existing Pulumi.yaml files
        var path = Path.Combine(Path.GetTempPath(), "pulumi-aspire", _projectName);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Executes the Pulumi program (up/deploy) via the Automation API.
    /// </summary>
    public Task<PulumiRunResult> RunAsync(
        Func<Task<IDictionary<string, object?>>> program,
        CancellationToken cancellationToken = default)
        => RunWithAutomationApiAsync(program, cancellationToken);

    /// <summary>
    /// Executes a Pulumi preview (dry run) via the Automation API.
    /// </summary>
    public Task<PulumiRunResult> PreviewAsync(
        Func<Task<IDictionary<string, object?>>> program,
        CancellationToken cancellationToken = default)
        => PreviewWithAutomationApiAsync(program, cancellationToken);

    private async Task<PulumiRunResult> RunWithAutomationApiAsync(
        Func<Task<IDictionary<string, object?>>> program,
        CancellationToken cancellationToken)
    {
        var pulumiFn = PulumiFn.Create(program);

        var stack = await LocalWorkspace.CreateOrSelectStackAsync(
            new InlineProgramArgs(_projectName, _stackName, pulumiFn)
            {
                WorkDir = GetWorkDir()
            },
            cancellationToken);

        if (_configureStack is not null)
        {
            await _configureStack(stack, cancellationToken);
        }

        var result = await stack.UpAsync(new UpOptions(), cancellationToken);

        return new PulumiRunResult
        {
            Success = true,
            Outputs = result.Outputs,
            Summary = result.Summary
        };
    }

    private async Task<PulumiRunResult> PreviewWithAutomationApiAsync(
        Func<Task<IDictionary<string, object?>>> program,
        CancellationToken cancellationToken)
    {
        var pulumiFn = PulumiFn.Create(program);

        var stack = await LocalWorkspace.CreateOrSelectStackAsync(
            new InlineProgramArgs(_projectName, _stackName, pulumiFn)
            {
                WorkDir = GetWorkDir()
            },
            cancellationToken);

        if (_configureStack is not null)
        {
            await _configureStack(stack, cancellationToken);
        }

        var result = await stack.PreviewAsync(new PreviewOptions(), cancellationToken);

        return new PulumiRunResult
        {
            Success = true,
            ChangeSummary = result.ChangeSummary
        };
    }
}

/// <summary>
/// Result of a Pulumi program execution.
/// </summary>
public sealed class PulumiRunResult
{
    /// <summary>
    /// Gets or sets whether the execution succeeded.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the error message if execution failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Gets or sets the stack outputs.
    /// </summary>
    public IImmutableDictionary<string, OutputValue>? Outputs { get; set; }

    /// <summary>
    /// Gets or sets the update summary.
    /// </summary>
    public UpdateSummary? Summary { get; set; }

    /// <summary>
    /// Gets or sets the change summary for preview.
    /// </summary>
    public IImmutableDictionary<OperationType, int>? ChangeSummary { get; set; }
}
