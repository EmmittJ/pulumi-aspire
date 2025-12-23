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
/// This class provides the common infrastructure for cloud environments:
/// </para>
/// <list type="bullet">
/// <item>Container registry management via <see cref="PulumiContainerRegistryResource"/></item>
/// <item>Environment resource management via <see cref="PulumiEnvironmentResource"/></item>
/// <item>Pipeline step orchestration for multi-stage deployments</item>
/// <item>Implementation of <see cref="IComputeEnvironmentResource"/> and <see cref="IContainerRegistry"/></item>
/// </list>
/// <para>
/// Derived classes should:
/// </para>
/// <list type="number">
/// <item>Create a <see cref="PulumiContainerRegistryResource"/> for the cloud provider</item>
/// <item>Implement <see cref="PulumiEnvironmentResource.CreateResourcesAsync"/> to create compute resources</item>
/// <item>Provide cloud-specific extension methods for configuration</item>
/// </list>
/// <para>
/// <strong>Naming Convention:</strong> All stacks are grouped under the same Pulumi project (<see cref="PulumiProjectName"/>).
/// Stack names use <see cref="ResourcePrefix"/> as the base:
/// </para>
/// <list type="bullet">
/// <item><c>{ResourcePrefix}-registry</c> for the container registry stack</item>
/// <item><c>{ResourcePrefix}</c> for the main environment stack</item>
/// </list>
/// </remarks>
/// <example>
/// <code>
/// public sealed class PulumiAzureContainerAppEnvironmentResource
///     : PulumiCloudEnvironmentResource
/// {
///     public PulumiAzureContainerAppEnvironmentResource(string name)
///         : base(name, new PulumiAzureContainerRegistryResource($"{name}-registry"))
///     {
///     }
///
///     public override Task CreateResourcesAsync(PulumiPublishingContext context)
///     {
///         // Create Azure Container Apps resources
///     }
/// }
/// </code>
/// </example>
public abstract class PulumiCloudEnvironmentResource :
    Resource,
    IComputeEnvironmentResource,
    IContainerRegistry,
    IPulumiEnvironmentResource
{
    /// <summary>
    /// Gets the container registry resource.
    /// </summary>
    /// <remarks>
    /// This resource manages the container registry configuration. It is created as a separate
    /// Pulumi stack with its own state file and is deployed before the main environment.
    /// </remarks>
    public IContainerRegistry ContainerRegistry { get; }

    /// <summary>
    /// Gets or sets the Pulumi project name.
    /// </summary>
    /// <remarks>
    /// All stacks for this environment are grouped under this project name in the Pulumi console.
    /// If not set, defaults to <see cref="EnvironmentName"/>.
    /// </remarks>
    public string PulumiProjectName { get; set; }

    /// <summary>
    /// Gets the environment name (e.g., "dev", "staging", "prod").
    /// </summary>
    /// <remarks>
    /// This is the name of the Aspire resource. Combined with <see cref="PulumiProjectName"/>
    /// to form the <see cref="ResourcePrefix"/>.
    /// </remarks>
    public string EnvironmentName { get; }

    /// <summary>
    /// Gets the computed resource prefix used for stack naming and cloud resource naming.
    /// </summary>
    /// <remarks>
    /// This is computed as <c>{PulumiProjectName}-{EnvironmentName}</c> and is used for:
    /// <list type="bullet">
    /// <item>Pulumi stack naming (environment stack: ResourcePrefix, registry stack: {ResourcePrefix}-registry)</item>
    /// <item>Cloud resource naming (resource groups, container registries, etc.)</item>
    /// <item>Deterministic random suffix generation</item>
    /// </list>
    /// </remarks>
    public string ResourcePrefix => $"{PulumiProjectName}-{EnvironmentName}";

    // IContainerRegistry implementation - delegate to the underlying registry
    ReferenceExpression IContainerRegistry.Name => ContainerRegistry.Name;
    ReferenceExpression IContainerRegistry.Endpoint => ContainerRegistry.Endpoint;

    /// <summary>
    /// Initializes a new instance of the <see cref="PulumiCloudEnvironmentResource"/> class.
    /// </summary>
    /// <param name="name">The name of the environment resource (used as <see cref="EnvironmentName"/>).</param>
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

        // EnvironmentName is the resource name (e.g., "dev", "staging", "prod")
        EnvironmentName = name;
        
        // PulumiProjectName defaults to EnvironmentName unless explicitly provided
        PulumiProjectName = projectName ?? EnvironmentName;

        // Add ContainerRegistryReferenceAnnotation for lookup
        Annotations.Add(new ContainerRegistryReferenceAnnotation(ContainerRegistry));

        // Add pipeline step annotation to create and expand steps
        Annotations.Add(new PipelineStepAnnotation(CreatePipelineStepsAsync));
    }

    /// <summary>
    /// Creates the pipeline steps for this cloud environment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method creates environment-specific deploy steps.
    /// </para>
    /// <para>
    /// Note: Container registry steps are NOT expanded here because the registry is added
    /// to the app model as a separate resource, and its steps are discovered automatically.
    /// </para>
    /// </remarks>
    protected virtual async Task<IEnumerable<PipelineStep>> CreatePipelineStepsAsync(
        PipelineStepFactoryContext factoryContext)
    {
        var steps = new List<PipelineStep>();

        // Add environment-specific steps
        // Container registry steps are discovered automatically since the registry
        // is added to the app model as a separate resource
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

    /// <summary>
    /// Computes the host URL <see cref="ReferenceExpression"/> for the given <see cref="EndpointReference"/>.
    /// Override in derived classes to provide cloud-specific endpoint resolution.
    /// </summary>
    /// <param name="endpointReference">The endpoint reference to compute the host address for.</param>
    /// <returns>A <see cref="ReferenceExpression"/> representing the host address.</returns>
    public abstract ReferenceExpression GetHostAddressExpression(EndpointReference endpointReference);

    /// <summary>
    /// Creates cloud resources within the Pulumi program callback.
    /// Override in derived classes to create cloud-specific compute resources.
    /// </summary>
    /// <param name="context">The publishing context with access to the model and translated resources.</param>
    /// <returns>A task that completes when all resources are created.</returns>
    public abstract Task CreateResourcesAsync(PulumiPublishingContext context);
}

/// <summary>
/// Options for configuring a <see cref="PulumiCloudEnvironmentResource"/>.
/// </summary>
public sealed class PulumiCloudEnvironmentOptions
{
    /// <summary>
    /// Gets or sets the callback to perform container registry login.
    /// </summary>
    /// <remarks>
    /// Use helper methods from <see cref="PulumiContainerRegistryHelpers"/> to create
    /// login callbacks for specific cloud providers (Azure CLI, AWS ECR, Docker).
    /// </remarks>
    public Func<PipelineStepContext, IContainerRegistry, Task>? RegistryLoginCallback { get; set; }
}
