// Licensed under the MIT License.

#pragma warning disable ASPIRECOMPUTE001 // GetComputeResources / compute-resource APIs are experimental
#pragma warning disable ASPIRECOMPUTE002 // IComputeEnvironmentResource is experimental

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;
using Pulumi;

namespace EmmittJ.Aspire.Hosting.Pulumi;

/// <summary>
/// State shared with the adoption program while the Pulumi program runs for an adopted native environment.
/// </summary>
/// <remarks>
/// A single instance is created per Pulumi operation (preview, up, destroy) and passed to the program
/// delegate supplied to <c>PublishAsPulumi</c>. The program uses it to walk the provisioning model that the
/// adopted native environment's modeling steps materialized (Bicep-backed deployment targets for Azure
/// Container Apps, deployment targets for Kubernetes/Docker Compose) and to export stack outputs.
/// </remarks>
public sealed class PulumiAdoptionContext
{
    private readonly Dictionary<string, Output<string>> _outputs = [];

    internal PulumiAdoptionContext(
        DistributedApplicationModel model,
        PulumiBackendResource backend,
        PulumiOperation operation,
        DistributedApplicationExecutionContext executionContext,
        IServiceProvider services,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        Model = model;
        Backend = backend;
        Operation = operation;
        ExecutionContext = executionContext;
        Services = services;
        Logger = logger;
        CancellationToken = cancellationToken;
    }

    /// <summary>Gets the distributed application model.</summary>
    public DistributedApplicationModel Model { get; }

    /// <summary>Gets the Pulumi backend resource that owns this operation.</summary>
    public PulumiBackendResource Backend { get; }

    /// <summary>Gets the adopted native compute environment resource.</summary>
    public IComputeEnvironmentResource AdoptedEnvironment => Backend.AdoptedEnvironment;

    /// <summary>
    /// Gets the Pulumi operation this program run is part of. Publish previews run as
    /// <see cref="PulumiOperation.Preview"/> and must not require cloud credentials; deploys run as
    /// <see cref="PulumiOperation.Up"/>.
    /// </summary>
    public PulumiOperation Operation { get; }

    /// <summary>Gets the execution context (publish/deploy) for resolving callback values.</summary>
    public DistributedApplicationExecutionContext ExecutionContext { get; }

    /// <summary>Gets the service provider for the running pipeline step.</summary>
    public IServiceProvider Services { get; }

    /// <summary>Gets the logger.</summary>
    public ILogger Logger { get; }

    /// <summary>Gets the cancellation token for the operation.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Gets the stack outputs registered so far, keyed by output name.</summary>
    public IReadOnlyDictionary<string, Output<string>> Outputs => _outputs;

    /// <summary>
    /// Enumerates the deployment targets that the adopted native environment's modeling steps attached to
    /// the application model's compute resources.
    /// </summary>
    /// <returns>
    /// Each targeted compute resource paired with the <see cref="DeploymentTargetAnnotation"/> attached for
    /// the adopted environment. For Azure Container Apps the annotation's target is an
    /// <c>AzureProvisioningResource</c> exposing Bicep; for Kubernetes/Docker Compose it is the
    /// provider-specific service resource.
    /// </returns>
    public IEnumerable<(IComputeResource Compute, DeploymentTargetAnnotation Target)> GetDeploymentTargets()
    {
        foreach (var resource in Model.GetComputeResources())
        {
            if (resource is not IComputeResource compute)
            {
                continue;
            }

            if (compute.GetDeploymentTargetAnnotation(AdoptedEnvironment) is { } annotation)
            {
                yield return (compute, annotation);
            }
        }
    }

    /// <summary>Exports a stack output.</summary>
    /// <param name="name">The output name.</param>
    /// <param name="value">The output value.</param>
    public void AddOutput(string name, Output<string> value) => _outputs[name] = value;

    /// <summary>Exports a literal stack output.</summary>
    /// <param name="name">The output name.</param>
    /// <param name="value">The output value.</param>
    public void AddOutput(string name, string value) => _outputs[name] = Output.Create(value);

    internal IDictionary<string, object?> BuildOutputs()
    {
        var outputs = new Dictionary<string, object?>(_outputs.Count);
        foreach (var (name, value) in _outputs)
        {
            outputs[name] = value;
        }

        return outputs;
    }
}
