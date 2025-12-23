// Licensed under the Apache License, Version 2.0.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIRECOMPUTE003

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;

namespace EmmittJ.Aspire.Hosting.Pulumi;

/// <summary>
/// Abstract base class for cloud-specific Pulumi environments that manage container registries
/// and compute environments (like Azure Container Apps, AWS ECS, or GCP Cloud Run).
/// </summary>
/// <remarks>
/// <para>
/// This class extends <see cref="PulumiEnvironmentResource"/> with container registry support
/// and implements <see cref="IComputeEnvironmentResource"/> for Aspire integration.
/// </para>
/// <para>
/// Derived classes should:
/// </para>
/// <list type="number">
/// <item>Create a <see cref="PulumiContainerRegistryResource"/> for the cloud provider</item>
/// <item>Implement <see cref="PulumiEnvironmentResource.CreateResourcesAsync"/> to create compute resources</item>
/// <item>Override <see cref="AddEnvironmentStepsAsync"/> for custom pipeline steps</item>
/// </list>
/// </remarks>
public abstract class PulumiCloudEnvironmentResource :
    PulumiEnvironmentResource,
    IComputeEnvironmentResource,
    IContainerRegistry
{
    /// <summary>
    /// Gets the container registry resource.
    /// </summary>
    public IContainerRegistry ContainerRegistry { get; }

    // IContainerRegistry implementation - delegate to the underlying registry
    ReferenceExpression IContainerRegistry.Name => ContainerRegistry.Name;
    ReferenceExpression IContainerRegistry.Endpoint => ContainerRegistry.Endpoint;

    /// <summary>
    /// Initializes a new instance of the <see cref="PulumiCloudEnvironmentResource"/> class.
    /// </summary>
    /// <param name="name">The name of the environment resource.</param>
    /// <param name="containerRegistry">The container registry to use for this environment.</param>
    /// <param name="projectName">The Pulumi project name. If null, uses <paramref name="name"/>.</param>
    protected PulumiCloudEnvironmentResource(
        string name,
        IContainerRegistry containerRegistry,
        string? projectName = null)
        : base(name)
    {
        ArgumentNullException.ThrowIfNull(containerRegistry);
        ContainerRegistry = containerRegistry;

        if (projectName is not null)
        {
            PulumiProjectName = projectName;
        }

        // Add ContainerRegistryReferenceAnnotation for lookup
        Annotations.Add(new ContainerRegistryReferenceAnnotation(ContainerRegistry));
    }

    /// <summary>
    /// Creates the pipeline steps for this cloud environment.
    /// Overrides base to use <see cref="AddEnvironmentStepsAsync"/> pattern.
    /// </summary>
    protected override async Task<IEnumerable<PipelineStep>> CreatePipelineStepsAsync(
        PipelineStepFactoryContext factoryContext)
    {
        var steps = new List<PipelineStep>();
        await AddEnvironmentStepsAsync(factoryContext, steps).ConfigureAwait(false);
        return steps;
    }

    /// <summary>
    /// Adds environment-specific pipeline steps.
    /// Override in derived classes to add cloud-specific deployment steps.
    /// </summary>
    /// <param name="factoryContext">The pipeline step factory context.</param>
    /// <param name="steps">The list to add steps to.</param>
    protected abstract Task AddEnvironmentStepsAsync(
        PipelineStepFactoryContext factoryContext,
        List<PipelineStep> steps);
}

/// <summary>
/// Options for configuring a <see cref="PulumiCloudEnvironmentResource"/>.
/// </summary>
public sealed class PulumiCloudEnvironmentOptions
{
    /// <summary>
    /// Gets or sets the callback to perform container registry login.
    /// </summary>
    public Func<PipelineStepContext, IContainerRegistry, Task>? RegistryLoginCallback { get; set; }
}
