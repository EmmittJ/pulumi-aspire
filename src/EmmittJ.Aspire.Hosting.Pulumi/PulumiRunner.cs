// Licensed under the Apache License, Version 2.0.

using System.Collections.Immutable;
using Pulumi.Automation;

namespace EmmittJ.Aspire.Hosting.Pulumi;

/// <summary>
/// Specifies how PulumiRunner should execute a Pulumi program.
/// </summary>
public enum PulumiRunnerMode
{
    /// <summary>
    /// Automatically detect the execution mode based on environment.
    /// Uses engine mode when PULUMI_MONITOR is set, otherwise uses Automation API.
    /// </summary>
    Auto,

    /// <summary>
    /// Force execution via the Pulumi engine (requires PULUMI_MONITOR to be set).
    /// Use this when running under the Pulumi CLI (pulumi up, pulumi preview).
    /// </summary>
    Engine,

    /// <summary>
    /// Force execution via the Automation API.
    /// Creates/manages its own stack independently of any parent Pulumi operation.
    /// Use this for nested stacks or standalone deployments.
    /// </summary>
    AutomationApi
}

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
///     .WithMode(PulumiRunnerMode.Auto)
///     .WithConfiguration(ConfigureStackAsync)
///     .RunAsync(program, cancellationToken);
/// </code>
/// </example>
/// </remarks>
public sealed class PulumiRunner
{
    private readonly PulumiEngineContext _engineContext;

    /// <summary>
    /// Initializes a new instance of <see cref="PulumiRunner"/>.
    /// </summary>
    /// <param name="engineContext">The Pulumi engine context.</param>
    public PulumiRunner(PulumiEngineContext engineContext)
    {
        _engineContext = engineContext ?? throw new ArgumentNullException(nameof(engineContext));
    }

    /// <summary>
    /// Gets the engine context.
    /// </summary>
    public PulumiEngineContext EngineContext => _engineContext;

    /// <summary>
    /// Creates a runner configured for the specified project and stack.
    /// </summary>
    /// <param name="projectName">The Pulumi project name.</param>
    /// <param name="stackName">The Pulumi stack name.</param>
    /// <returns>A builder for further configuration.</returns>
    public PulumiStackRunnerBuilder ForStack(string projectName, string stackName)
    {
        return new PulumiStackRunnerBuilder(_engineContext, projectName, stackName);
    }

    internal bool ShouldUseEngine(PulumiRunnerMode mode)
    {
        return mode switch
        {
            PulumiRunnerMode.Engine => _engineContext.IsRunningUnderEngine
                ? true
                : throw new InvalidOperationException(
                    "Engine mode requested but not running under Pulumi engine. " +
                    "Ensure PULUMI_MONITOR environment variable is set."),
            PulumiRunnerMode.AutomationApi => false,
            PulumiRunnerMode.Auto => _engineContext.IsRunningUnderEngine,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown runner mode")
        };
    }
}

/// <summary>
/// Builder for creating a configured <see cref="PulumiStackRunner"/>.
/// </summary>
public sealed class PulumiStackRunnerBuilder
{
    private readonly PulumiEngineContext _engineContext;
    private readonly string _projectName;
    private readonly string _stackName;
    private PulumiRunnerMode _mode = PulumiRunnerMode.Auto;
    private string? _explicitWorkDir;
    private Func<WorkspaceStack, CancellationToken, Task>? _configureStack;

    internal PulumiStackRunnerBuilder(PulumiEngineContext engineContext, string projectName, string stackName)
    {
        _engineContext = engineContext;
        _projectName = projectName;
        _stackName = stackName;
    }

    /// <summary>
    /// Sets the execution mode.
    /// </summary>
    public PulumiStackRunnerBuilder WithMode(PulumiRunnerMode mode)
    {
        _mode = mode;
        return this;
    }

    /// <summary>
    /// Sets an explicit working directory for Pulumi operations.
    /// Only used in Automation API mode. If not set (or null), a temp directory is used.
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
    /// Sets a callback to configure the stack before operations (Automation API mode only).
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
            _engineContext,
            _projectName,
            _stackName,
            _mode,
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
    private readonly PulumiEngineContext _engineContext;
    private readonly string _projectName;
    private readonly string _stackName;
    private readonly PulumiRunnerMode _mode;
    private readonly string? _explicitWorkDir;
    private readonly Func<WorkspaceStack, CancellationToken, Task>? _configureStack;

    internal PulumiStackRunner(
        PulumiEngineContext engineContext,
        string projectName,
        string stackName,
        PulumiRunnerMode mode,
        string? explicitWorkDir,
        Func<WorkspaceStack, CancellationToken, Task>? configureStack)
    {
        _engineContext = engineContext;
        _projectName = projectName;
        _stackName = stackName;
        _mode = mode;
        _explicitWorkDir = explicitWorkDir;
        _configureStack = configureStack;
    }

    /// <summary>
    /// Gets the working directory for Automation API operations.
    /// Uses explicit work dir if set, otherwise creates a temp directory.
    /// </summary>
    private string GetAutomationApiWorkDir()
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
    /// Executes the Pulumi program (up/deploy).
    /// </summary>
    public async Task<PulumiRunResult> RunAsync(
        Func<Task<IDictionary<string, object?>>> program,
        CancellationToken cancellationToken = default)
    {
        var useEngine = ShouldUseEngine();

        if (useEngine)
        {
            return await RunWithEngineAsync(program);
        }

        return await RunWithAutomationApiAsync(program, cancellationToken);
    }

    /// <summary>
    /// Executes a Pulumi preview (dry run).
    /// </summary>
    public async Task<PulumiRunResult> PreviewAsync(
        Func<Task<IDictionary<string, object?>>> program,
        CancellationToken cancellationToken = default)
    {
        var useEngine = ShouldUseEngine();

        if (useEngine)
        {
            return await RunWithEngineAsync(program);
        }

        return await PreviewWithAutomationApiAsync(program, cancellationToken);
    }

    private bool ShouldUseEngine()
    {
        return _mode switch
        {
            PulumiRunnerMode.Engine => _engineContext.IsRunningUnderEngine
                ? true
                : throw new InvalidOperationException(
                    "Engine mode requested but not running under Pulumi engine. " +
                    "Ensure PULUMI_MONITOR environment variable is set."),
            PulumiRunnerMode.AutomationApi => false,
            PulumiRunnerMode.Auto => _engineContext.IsRunningUnderEngine,
            _ => throw new ArgumentOutOfRangeException(nameof(_mode), _mode, "Unknown runner mode")
        };
    }

    private static async Task<PulumiRunResult> RunWithEngineAsync(
        Func<Task<IDictionary<string, object?>>> program)
    {
        var exitCode = await global::Pulumi.Deployment.RunAsync(program);

        if (exitCode != 0)
        {
            return new PulumiRunResult
            {
                Success = false,
                ErrorMessage = $"Pulumi deployment failed with exit code {exitCode}",
                ExecutionMode = PulumiRunnerMode.Engine
            };
        }

        return new PulumiRunResult
        {
            Success = true,
            ExecutionMode = PulumiRunnerMode.Engine
        };
    }

    private async Task<PulumiRunResult> RunWithAutomationApiAsync(
        Func<Task<IDictionary<string, object?>>> program,
        CancellationToken cancellationToken)
    {
        var pulumiFn = PulumiFn.Create(program);

        var stack = await LocalWorkspace.CreateOrSelectStackAsync(
            new InlineProgramArgs(_projectName, _stackName, pulumiFn)
            {
                WorkDir = GetAutomationApiWorkDir()
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
            ExecutionMode = PulumiRunnerMode.AutomationApi,
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
                WorkDir = GetAutomationApiWorkDir()
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
            ExecutionMode = PulumiRunnerMode.AutomationApi,
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
    /// Gets or sets the execution mode used (Engine or AutomationApi).
    /// </summary>
    public PulumiRunnerMode ExecutionMode { get; set; }

    /// <summary>
    /// Gets or sets the stack outputs (Automation API mode only).
    /// </summary>
    public IImmutableDictionary<string, OutputValue>? Outputs { get; set; }

    /// <summary>
    /// Gets or sets the update summary (Automation API mode only).
    /// </summary>
    public UpdateSummary? Summary { get; set; }

    /// <summary>
    /// Gets or sets the change summary for preview (Automation API mode only).
    /// </summary>
    public IImmutableDictionary<OperationType, int>? ChangeSummary { get; set; }
}
