// Licensed under the Apache License, Version 2.0.

#pragma warning disable ASPIREPIPELINES001 // Pipeline APIs are experimental

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Logging;
using Pulumi;
using PulumiResource = Pulumi.Resource;

namespace EmmittJ.Aspire.Hosting.Pulumi;

/// <summary>
/// Context for publishing Aspire resources to Pulumi.
/// Holds state during the Pulumi program callback and provides access to translated resources.
/// </summary>
/// <remarks>
/// <para>
/// This context is created by the base <see cref="PulumiEnvironmentResource"/> and passed
/// to provider-specific implementations for creating cloud resources.
/// </para>
/// <para>
/// The context maintains lookups for translated Pulumi resources, enabling cross-resource
/// references and dependency resolution.
/// </para>
/// </remarks>
public sealed class PulumiPublishingContext
{
    private readonly Dictionary<IResource, PulumiResource> _translatedResources = [];
    private readonly Dictionary<string, Output<string>> _outputs = [];
    private readonly Dictionary<string, object> _configValues = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="PulumiPublishingContext"/> class.
    /// </summary>
    /// <param name="model">The distributed application model.</param>
    /// <param name="environment">The Pulumi environment resource.</param>
    /// <param name="pipelineContext">The pipeline step context.</param>
    /// <param name="executionContext">The execution context.</param>
    /// <param name="logger">The logger instance.</param>
    public PulumiPublishingContext(
        DistributedApplicationModel model,
        IPulumiEnvironmentResource environment,
        PipelineStepContext pipelineContext,
        DistributedApplicationExecutionContext executionContext,
        ILogger logger)
    {
        Model = model;
        Environment = environment;
        PipelineContext = pipelineContext;
        ExecutionContext = executionContext;
        Logger = logger;
    }

    /// <summary>
    /// Gets the distributed application model.
    /// </summary>
    public DistributedApplicationModel Model { get; }

    /// <summary>
    /// Gets the Pulumi environment resource.
    /// </summary>
    public IPulumiEnvironmentResource Environment { get; }

    /// <summary>
    /// Gets the pipeline step context.
    /// </summary>
    public PipelineStepContext PipelineContext { get; }

    /// <summary>
    /// Gets the execution context for processing environment callbacks.
    /// </summary>
    public DistributedApplicationExecutionContext ExecutionContext { get; }

    /// <summary>
    /// Gets the logger instance.
    /// </summary>
    public ILogger Logger { get; }

    /// <summary>
    /// Gets the stack configuration values set during deployment.
    /// </summary>
    public IReadOnlyDictionary<string, object> ConfigValues => _configValues;

    /// <summary>
    /// Gets the translated Pulumi resources keyed by their source Aspire resource.
    /// </summary>
    public IReadOnlyDictionary<IResource, PulumiResource> TranslatedResources => _translatedResources;

    /// <summary>
    /// Gets the outputs that will be exported from the stack.
    /// </summary>
    public IReadOnlyDictionary<string, Output<string>> Outputs => _outputs;

    /// <summary>
    /// Registers a translated Pulumi resource for the given Aspire resource.
    /// </summary>
    /// <param name="aspireResource">The source Aspire resource.</param>
    /// <param name="pulumiResource">The translated Pulumi resource.</param>
    public void RegisterResource(IResource aspireResource, PulumiResource pulumiResource)
    {
        _translatedResources[aspireResource] = pulumiResource;
        Logger.LogDebug(
            "Registered Pulumi resource '{PulumiName}' for Aspire resource '{AspireName}'",
            pulumiResource.GetResourceName(),
            aspireResource.Name);
    }

    /// <summary>
    /// Gets a previously translated Pulumi resource for the given Aspire resource.
    /// </summary>
    /// <typeparam name="T">The expected Pulumi resource type.</typeparam>
    /// <param name="aspireResource">The source Aspire resource.</param>
    /// <returns>The translated Pulumi resource, or null if not found.</returns>
    public T? GetResource<T>(IResource aspireResource) where T : PulumiResource
    {
        return _translatedResources.TryGetValue(aspireResource, out var resource)
            ? resource as T
            : null;
    }

    /// <summary>
    /// Adds a stack output that will be exported after deployment.
    /// </summary>
    /// <param name="name">The output name.</param>
    /// <param name="value">The output value.</param>
    public void AddOutput(string name, Output<string> value)
    {
        _outputs[name] = value;
        Logger.LogDebug("Added stack output '{OutputName}'", name);
    }

    /// <summary>
    /// Adds a stack output that will be exported after deployment.
    /// </summary>
    /// <param name="name">The output name.</param>
    /// <param name="value">The output value.</param>
    public void AddOutput(string name, string value)
    {
        _outputs[name] = Output.Create(value);
        Logger.LogDebug("Added stack output '{OutputName}'", name);
    }

    /// <summary>
    /// Sets a configuration value for the stack.
    /// </summary>
    /// <param name="key">The configuration key.</param>
    /// <param name="value">The configuration value.</param>
    /// <param name="isSecret">Whether the value is a secret.</param>
    public void SetConfig(string key, object value, bool isSecret = false)
    {
        _configValues[key] = value;
        Logger.LogDebug(
            "Set config '{Key}' = {Value}",
            key,
            isSecret ? "***" : value);
    }

    /// <summary>
    /// Builds the outputs dictionary for the Pulumi program.
    /// </summary>
    /// <returns>A dictionary of output name to output value.</returns>
    public IDictionary<string, object?> BuildOutputs()
    {
        var outputs = new Dictionary<string, object?>();
        foreach (var (name, value) in _outputs)
        {
            outputs[name] = value;
        }
        return outputs;
    }
}
