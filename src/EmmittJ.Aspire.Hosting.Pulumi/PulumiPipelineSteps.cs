// Licensed under the Apache License, Version 2.0.

namespace EmmittJ.Aspire.Hosting.Pulumi;

/// <summary>
/// Well-known pipeline step name helpers and tags used by the Pulumi integration.
/// </summary>
/// <remarks>
/// Step names are scoped per environment resource using the pattern <c>{prefix}-{resourceName}</c>
/// so that multiple Pulumi environments in the same app model do not collide.
/// </remarks>
public static class PulumiPipelineSteps
{
    /// <summary>Tag applied to all Pulumi deploy steps.</summary>
    public const string PulumiTag = "pulumi";

    /// <summary>Tag applied to per-resource print-summary steps.</summary>
    public const string PrintSummaryTag = "print-summary";

    /// <summary>Gets the name of the step that materializes deployment targets for an environment.</summary>
    public static string PrepareDeploymentTargets(string environmentName) => $"pulumi-prepare-{environmentName}";

    /// <summary>Gets the name of the step that writes the reviewable publish artifact for an environment.</summary>
    public static string Publish(string environmentName) => $"pulumi-publish-{environmentName}";

    /// <summary>Gets the name of the step that deploys an environment's Pulumi stack.</summary>
    public static string Deploy(string environmentName) => $"pulumi-deploy-{environmentName}";

    /// <summary>Gets the name of the step that destroys an environment's Pulumi stack.</summary>
    public static string Destroy(string environmentName) => $"pulumi-destroy-{environmentName}";

    /// <summary>Gets the name of the step that provisions a container registry stack.</summary>
    public static string DeployRegistry(string registryName) => $"pulumi-deploy-registry-{registryName}";

    /// <summary>Gets the name of the step that destroys a container registry stack.</summary>
    public static string DestroyRegistry(string registryName) => $"pulumi-destroy-registry-{registryName}";

    /// <summary>Gets the name of the step that authenticates to a container registry.</summary>
    public static string LoginRegistry(string registryName) => $"pulumi-login-registry-{registryName}";

    /// <summary>Gets the name of the per-resource container image push step.</summary>
    public static string PushImage(string resourceName) => $"pulumi-push-{resourceName}";

    /// <summary>Gets the name of the per-resource print-summary step.</summary>
    public static string PrintSummary(string resourceName) => $"pulumi-print-{resourceName}-summary";
}
