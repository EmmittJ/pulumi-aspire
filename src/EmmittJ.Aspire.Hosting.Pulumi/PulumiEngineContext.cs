// Licensed under the Apache License, Version 2.0.

using Microsoft.Extensions.Configuration;

namespace EmmittJ.Aspire.Hosting.Pulumi;

/// <summary>
/// Provides information about the Pulumi engine context when running under a language host.
/// </summary>
/// <remarks>
/// <para>
/// When Aspire is invoked by a Pulumi language host (via <c>pulumi up</c> or <c>pulumi preview</c>),
/// the engine sets specific environment variables that allow the .NET program to communicate
/// directly with the Pulumi engine's monitor.
/// </para>
/// <para>
/// This class detects whether we're running under a Pulumi engine and provides the context
/// needed to register resources directly with the engine instead of creating a separate
/// Automation API stack.
/// </para>
/// <para>
/// Configuration keys (from environment variables via IConfiguration):
/// </para>
/// <list type="bullet">
/// <item><c>PULUMI_MONITOR</c> - Monitor gRPC address</item>
/// <item><c>PULUMI_ENGINE</c> - Engine gRPC address</item>
/// <item><c>PULUMI_PROJECT</c> - Project name</item>
/// <item><c>PULUMI_STACK</c> - Stack name</item>
/// <item><c>PULUMI_ORGANIZATION</c> - Organization name</item>
/// <item><c>PULUMI_ROOT_DIRECTORY</c> - Root directory</item>
/// <item><c>PULUMI_PWD</c> - Working directory</item>
/// <item><c>PULUMI_DRY_RUN</c> - Whether this is a preview</item>
/// <item><c>PULUMI_PARALLEL</c> - Parallelism level</item>
/// <item><c>PULUMI_CONFIG</c> - Configuration JSON</item>
/// <item><c>PULUMI_CONFIG_SECRET_KEYS</c> - Secret keys JSON</item>
/// </list>
/// </remarks>
public sealed class PulumiEngineContext
{
    /// <summary>
    /// Gets whether we're running under a Pulumi engine (language host).
    /// </summary>
    public bool IsRunningUnderEngine { get; }

    /// <summary>
    /// Gets the monitor gRPC address for resource registration.
    /// </summary>
    public string? MonitorAddress { get; }

    /// <summary>
    /// Gets the engine gRPC address for engine operations.
    /// </summary>
    public string? EngineAddress { get; }

    /// <summary>
    /// Gets the project name.
    /// </summary>
    public string? Project { get; }

    /// <summary>
    /// Gets the stack name.
    /// </summary>
    public string? Stack { get; }

    /// <summary>
    /// Gets the organization name.
    /// </summary>
    public string? Organization { get; }

    /// <summary>
    /// Gets the root directory.
    /// </summary>
    public string? RootDirectory { get; }

    /// <summary>
    /// Gets the current working directory.
    /// </summary>
    public string? WorkingDirectory { get; }

    /// <summary>
    /// Gets whether this is a dry run (preview).
    /// </summary>
    public bool IsDryRun { get; }

    /// <summary>
    /// Gets the parallelism level.
    /// </summary>
    public int Parallel { get; }

    /// <summary>
    /// Gets the configuration dictionary.
    /// </summary>
    public IReadOnlyDictionary<string, string> Config { get; }

    /// <summary>
    /// Gets the set of secret configuration keys.
    /// </summary>
    public IReadOnlySet<string> ConfigSecretKeys { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="PulumiEngineContext"/> from <see cref="IConfiguration"/>.
    /// </summary>
    /// <param name="configuration">The configuration to read Pulumi environment variables from.</param>
    public PulumiEngineContext(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        MonitorAddress = configuration["PULUMI_MONITOR"];
        EngineAddress = configuration["PULUMI_ENGINE"];
        Project = configuration["PULUMI_PROJECT"];
        Stack = configuration["PULUMI_STACK"];
        Organization = configuration["PULUMI_ORGANIZATION"];
        RootDirectory = configuration["PULUMI_ROOT_DIRECTORY"];
        WorkingDirectory = configuration["PULUMI_PWD"];

        IsRunningUnderEngine = !string.IsNullOrEmpty(MonitorAddress) && !string.IsNullOrEmpty(EngineAddress);

        IsDryRun = bool.TryParse(configuration["PULUMI_DRY_RUN"], out var dryRunValue) && dryRunValue;
        Parallel = int.TryParse(configuration["PULUMI_PARALLEL"], out var parallelValue) ? parallelValue : 0;

        Config = ParseConfigJson(configuration["PULUMI_CONFIG"]);
        ConfigSecretKeys = ParseConfigSecretKeysJson(configuration["PULUMI_CONFIG_SECRET_KEYS"]);
    }

    private static IReadOnlyDictionary<string, string> ParseConfigJson(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return new Dictionary<string, string>();
        }

        try
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return parsed ?? new Dictionary<string, string>();
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }

    private static IReadOnlySet<string> ParseConfigSecretKeysJson(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return new HashSet<string>();
        }

        try
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json);
            return parsed is not null ? new HashSet<string>(parsed) : new HashSet<string>();
        }
        catch
        {
            return new HashSet<string>();
        }
    }
}
