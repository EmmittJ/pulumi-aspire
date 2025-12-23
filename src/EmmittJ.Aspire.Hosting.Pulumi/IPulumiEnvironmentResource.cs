// Licensed under the Apache License, Version 2.0.

using Aspire.Hosting.ApplicationModel;
using Pulumi.Automation;

namespace EmmittJ.Aspire.Hosting.Pulumi;

/// <summary>
/// Marker interface for Pulumi-managed compute environments.
/// Resources implementing this interface deploy to cloud infrastructure using Pulumi.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="ResourcePrefix"/> is computed from <see cref="PulumiProjectName"/> and
/// <see cref="EnvironmentName"/> using the format <c>{PulumiProjectName}-{EnvironmentName}</c>.
/// Stack names are derived from it: the environment stack uses ResourcePrefix directly,
/// and the registry stack uses <c>{ResourcePrefix}-registry</c>.
/// </para>
/// <para>
/// Implementations must provide pipeline steps for deployment operations and
/// implement <see cref="GetHostAddressExpression"/> to enable service discovery.
/// </para>
/// <para>
/// <strong>Naming Convention:</strong> All stacks are grouped under <see cref="PulumiProjectName"/>.
/// Stack names follow the pattern:
/// <list type="bullet">
/// <item><c>{ResourcePrefix}-registry</c> for the container registry stack</item>
/// <item><c>{ResourcePrefix}</c> for the main environment stack</item>
/// </list>
/// </para>
/// </remarks>
public interface IPulumiEnvironmentResource : IComputeEnvironmentResource
{
    /// <summary>
    /// Gets the Pulumi project name. All stacks are grouped under this project in the Pulumi console.
    /// </summary>
    string PulumiProjectName { get; }

    /// <summary>
    /// Gets the environment name (e.g., "dev", "staging", "prod").
    /// </summary>
    /// <remarks>
    /// This is typically the name of the Aspire resource. Combined with <see cref="PulumiProjectName"/>
    /// to form the <see cref="ResourcePrefix"/>.
    /// </remarks>
    string EnvironmentName { get; }

    /// <summary>
    /// Gets the computed prefix for resource naming and stack identification.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is computed as <c>{PulumiProjectName}-{EnvironmentName}</c> and is used for:
    /// </para>
    /// <list type="bullet">
    /// <item>Pulumi stack naming (environment stack uses ResourcePrefix directly, registry stack uses {ResourcePrefix}-registry)</item>
    /// <item>Cloud resource naming (resource groups, container registries, managed environments, etc.)</item>
    /// <item>Deterministic random suffix generation (used as keeper value)</item>
    /// </list>
    /// </remarks>
    string ResourcePrefix => $"{PulumiProjectName}-{EnvironmentName}";

    /// <summary>
    /// Creates resources within the Pulumi program callback.
    /// This method is called inside the <see cref="PulumiFn"/> callback.
    /// </summary>
    /// <param name="context">The publishing context with access to the model and translated resources.</param>
    /// <returns>A task that completes when all resources are created.</returns>
    Task CreateResourcesAsync(PulumiPublishingContext context);
}
