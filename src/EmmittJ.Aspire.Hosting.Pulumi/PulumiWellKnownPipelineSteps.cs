// Licensed under the Apache License, Version 2.0.

#pragma warning disable ASPIREPIPELINES001

namespace EmmittJ.Aspire.Hosting.Pulumi;

/// <summary>
/// Defines well-known pipeline step name patterns used in Pulumi deployment pipelines.
/// </summary>
/// <remarks>
/// <para>
/// This class provides constants and helper methods for generating consistent pipeline step names
/// across Pulumi-managed resources. Step names follow the pattern: <c>{prefix}-{resourceName}</c>.
/// </para>
/// <para>
/// The step naming convention allows for:
/// </para>
/// <list type="bullet">
/// <item>Consistent naming across all Pulumi resources</item>
/// <item>Easy identification of Pulumi-specific steps in the pipeline</item>
/// <item>Predictable dependency references between steps</item>
/// </list>
/// </remarks>
public static class PulumiWellKnownPipelineSteps
{
    #region Step Name Prefixes

    /// <summary>
    /// Prefix for Pulumi deploy steps.
    /// Full step name: <c>pulumi-deploy-{resourceName}</c>
    /// </summary>
    public const string DeployPrefix = "pulumi-deploy";

    /// <summary>
    /// Prefix for Pulumi preview (dry-run) steps.
    /// Full step name: <c>pulumi-preview-{resourceName}</c>
    /// </summary>
    public const string PreviewPrefix = "pulumi-preview";

    /// <summary>
    /// Prefix for Pulumi destroy steps.
    /// Full step name: <c>pulumi-destroy-{resourceName}</c>
    /// </summary>
    public const string DestroyPrefix = "pulumi-destroy";

    /// <summary>
    /// Prefix for Pulumi registry deploy steps.
    /// Full step name: <c>pulumi-deploy-registry-{resourceName}</c>
    /// </summary>
    public const string DeployRegistryPrefix = "pulumi-deploy-registry";

    /// <summary>
    /// Prefix for container registry login steps.
    /// Full step name: <c>login-registry-{resourceName}</c>
    /// </summary>
    public const string LoginRegistryPrefix = "login-registry";

    /// <summary>
    /// Prefix for print summary steps.
    /// Full step name: <c>pulumi-print-summary-{resourceName}</c>
    /// </summary>
    public const string PrintSummaryPrefix = "pulumi-print-summary";

    #endregion

    #region Step Name Generators

    /// <summary>
    /// Gets the deploy step name for the specified resource.
    /// </summary>
    /// <param name="resourceName">The resource name.</param>
    /// <returns>The step name in format <c>pulumi-deploy-{resourceName}</c>.</returns>
    public static string Deploy(string resourceName) => $"{DeployPrefix}-{resourceName}";

    /// <summary>
    /// Gets the preview step name for the specified resource.
    /// </summary>
    /// <param name="resourceName">The resource name.</param>
    /// <returns>The step name in format <c>pulumi-preview-{resourceName}</c>.</returns>
    public static string Preview(string resourceName) => $"{PreviewPrefix}-{resourceName}";

    /// <summary>
    /// Gets the destroy step name for the specified resource.
    /// </summary>
    /// <param name="resourceName">The resource name.</param>
    /// <returns>The step name in format <c>pulumi-destroy-{resourceName}</c>.</returns>
    public static string Destroy(string resourceName) => $"{DestroyPrefix}-{resourceName}";

    /// <summary>
    /// Gets the registry deploy step name for the specified resource.
    /// </summary>
    /// <param name="resourceName">The resource name.</param>
    /// <returns>The step name in format <c>pulumi-deploy-registry-{resourceName}</c>.</returns>
    public static string DeployRegistry(string resourceName) => $"{DeployRegistryPrefix}-{resourceName}";

    /// <summary>
    /// Gets the registry login step name for the specified resource.
    /// </summary>
    /// <param name="resourceName">The resource name.</param>
    /// <returns>The step name in format <c>login-registry-{resourceName}</c>.</returns>
    public static string LoginRegistry(string resourceName) => $"{LoginRegistryPrefix}-{resourceName}";

    /// <summary>
    /// Gets the print summary step name for the specified resource.
    /// </summary>
    /// <param name="resourceName">The resource name.</param>
    /// <returns>The step name in format <c>pulumi-print-summary-{resourceName}</c>.</returns>
    public static string PrintSummary(string resourceName) => $"{PrintSummaryPrefix}-{resourceName}";

    #endregion

    #region Tags

    /// <summary>
    /// Tag for Pulumi-specific pipeline steps.
    /// </summary>
    public const string PulumiTag = "pulumi";

    /// <summary>
    /// Tag for Pulumi registry-related pipeline steps.
    /// </summary>
    public const string PulumiRegistryTag = "pulumi-registry";

    /// <summary>
    /// Tag for preview/dry-run steps.
    /// </summary>
    public const string PreviewTag = "pulumi-preview";

    /// <summary>
    /// Tag for destroy steps.
    /// </summary>
    public const string DestroyTag = "pulumi-destroy";

    /// <summary>
    /// Tag for print summary steps.
    /// </summary>
    public const string PrintSummaryTag = "print-summary";

    #endregion
}
