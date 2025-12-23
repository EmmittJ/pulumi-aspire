// Licensed under the Apache License, Version 2.0.

#pragma warning disable ASPIREPIPELINES001 // Pipeline APIs are experimental
#pragma warning disable ASPIREPIPELINES003 // IResourceContainerImageManager is experimental
#pragma warning disable ASPIRECOMPUTE003 // IContainerRegistry is experimental

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.DependencyInjection;
using Pulumi;
using AspireResource = Aspire.Hosting.ApplicationModel.Resource;

namespace EmmittJ.Aspire.Hosting.Pulumi;

/// <summary>
/// Represents a deployment target resource for Pulumi.
/// This is the resource attached via <see cref="DeploymentTargetAnnotation"/>.
/// </summary>
/// <remarks>
/// <para>
/// This class provides common functionality for deployment targets, including
/// output management, container image resolution, and pipeline step creation for image pushing.
/// </para>
/// <para>
/// Provider packages create derived types (e.g., PulumiContainerAppResource)
/// that add provider-specific properties and behaviors.
/// </para>
/// </remarks>
public class PulumiProvisioningResource : AspireResource
{
    private readonly Dictionary<string, Output<string>> _outputs = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="PulumiProvisioningResource"/> class.
    /// </summary>
    /// <param name="name">The name of this deployment target resource.</param>
    /// <param name="sourceResource">The source Aspire compute resource.</param>
    /// <param name="environment">The Pulumi environment resource.</param>
    public PulumiProvisioningResource(
        string name,
        IComputeResource sourceResource,
        IPulumiEnvironmentResource environment)
        : base(name)
    {
        SourceResource = sourceResource;
        Environment = environment;

        // Add pipeline step annotation for container image push (if this resource needs it)
        Annotations.Add(new PipelineStepAnnotation(CreatePipelineSteps));

        // Add pipeline configuration annotation to wire up dependencies
        Annotations.Add(new PipelineConfigurationAnnotation(ConfigurePipeline));
    }

    /// <summary>
    /// Gets the source Aspire compute resource.
    /// </summary>
    public IComputeResource SourceResource { get; }

    /// <summary>
    /// Gets the Pulumi environment this resource belongs to.
    /// </summary>
    public IPulumiEnvironmentResource Environment { get; }

    /// <summary>
    /// Gets the outputs registered for this resource.
    /// </summary>
    public IReadOnlyDictionary<string, Output<string>> Outputs => _outputs;

    /// <summary>
    /// Adds an output for this resource.
    /// </summary>
    /// <param name="name">The output name.</param>
    /// <param name="value">The output value.</param>
    public void AddOutput(string name, Output<string> value)
    {
        _outputs[name] = value;
    }

    /// <summary>
    /// Adds an output for this resource.
    /// </summary>
    /// <param name="name">The output name.</param>
    /// <param name="value">The output value.</param>
    public void AddOutput(string name, string value)
    {
        _outputs[name] = Output.Create(value);
    }

    /// <summary>
    /// Gets the container image for the source resource.
    /// </summary>
    /// <returns>The container image string, or null if not available.</returns>
    public string? GetContainerImage()
    {
        if (!SourceResource.TryGetAnnotationsOfType<ContainerImageAnnotation>(out var annotations))
        {
            return null;
        }

        var imageAnnotation = annotations.FirstOrDefault();
        if (imageAnnotation is null)
        {
            return null;
        }

        var registry = imageAnnotation.Registry;
        var image = imageAnnotation.Image;
        var tag = imageAnnotation.Tag ?? "latest";

        return string.IsNullOrEmpty(registry)
            ? $"{image}:{tag}"
            : $"{registry}/{image}:{tag}";
    }

    /// <summary>
    /// Gets a normalized name for the resource (lowercase with hyphens).
    /// </summary>
    public string NormalizedName => SourceResource.Name.ToLowerInvariant().Replace("_", "-");

    private IEnumerable<PipelineStep> CreatePipelineSteps(PipelineStepFactoryContext factoryContext)
    {
        // Only create push step if we have a target resource that needs image push
        if (!SourceResource.RequiresImageBuildAndPush())
        {
            return [];
        }

        // Get the registry from the source resource's deployment target annotation
        var deploymentTargetAnnotation = SourceResource.GetDeploymentTargetAnnotation();
        if (deploymentTargetAnnotation?.ContainerRegistry is not IContainerRegistry registry)
        {
            return [];
        }

        var steps = new List<PipelineStep>();

        // Create push step for this deployment target
        // Tagged with PushContainerImage so environments can hook into push steps
        var pushStep = new PipelineStep
        {
            Name = $"push-{SourceResource.Name}",
            Description = $"Pushes container image for {SourceResource.Name} to registry.",
            Action = ctx => PushImageToRegistryAsync(registry, SourceResource, ctx),
            Tags = [WellKnownPipelineTags.PushContainerImage],
            Resource = this
        };

        steps.Add(pushStep);

        return steps;
    }

    private static async Task PushImageToRegistryAsync(
        IContainerRegistry registry,
        IResource resource,
        PipelineStepContext context)
    {
        var containerImageManager = context.Services.GetRequiredService<IResourceContainerImageManager>();

        var registryEndpoint = await registry.Endpoint.GetValueAsync(context.CancellationToken).ConfigureAwait(false) ??
            throw new InvalidOperationException("Failed to retrieve container registry endpoint.");

        // Get the target tag using ContainerImageReference
        IValueProvider cir = new ContainerImageReference(resource);
        var targetTag = await cir.GetValueAsync(
            new ValueProviderContext { ExecutionContext = context.ExecutionContext },
            context.CancellationToken).ConfigureAwait(false);

        var pushTask = await context.ReportingStep.CreateTaskAsync(
            $"Pushing **{resource.Name}** to **{registryEndpoint}**",
            context.CancellationToken).ConfigureAwait(false);

        await using (pushTask.ConfigureAwait(false))
        {
            try
            {
                if (targetTag is null)
                {
                    throw new InvalidOperationException($"Failed to get target tag for {resource.Name}");
                }

                await containerImageManager.PushImageAsync(resource, context.CancellationToken).ConfigureAwait(false);

                await pushTask.CompleteAsync(
                    $"Successfully pushed **{resource.Name}** to `{targetTag}`",
                    CompletionState.Completed,
                    context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await pushTask.CompleteAsync(
                    $"Failed to push **{resource.Name}**: {ex.Message}",
                    CompletionState.CompletedWithError,
                    context.CancellationToken).ConfigureAwait(false);
                throw;
            }
        }
    }

    private void ConfigurePipeline(PipelineConfigurationContext context)
    {
        // Find the push step for this resource using tag
        var pushSteps = context.GetSteps(this, WellKnownPipelineTags.PushContainerImage);

        // Make push step depend on the Build meta-step (after all builds complete)
        pushSteps.DependsOn(WellKnownPipelineSteps.Build);

        // Make push step depend on the PushPrereq step (which waits for registry + login)
        pushSteps.DependsOn(WellKnownPipelineSteps.PushPrereq);

        // Push step is required by Push meta-step
        pushSteps.RequiredBy(WellKnownPipelineSteps.Push);
    }
}
