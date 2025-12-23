// Licensed under the Apache License, Version 2.0.

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Microsoft.Extensions.Logging;

namespace EmmittJ.Aspire.Hosting.Pulumi.Azure;

/// <summary>
/// Event subscriber that processes compute resources before the application starts.
/// This follows the Aspire-native pattern used by Azure Container Apps, Kubernetes, etc.
/// </summary>
/// <remarks>
/// <para>
/// This class implements <see cref="IDistributedApplicationEventingSubscriber"/> and subscribes 
/// to <see cref="BeforeStartEvent"/> to iterate over all compute resources, creating deployment 
/// targets and attaching <see cref="DeploymentTargetAnnotation"/> to each one.
/// </para>
/// <para>
/// This pattern is identical to how <c>KubernetesInfrastructure</c> and 
/// <c>AzureContainerAppsInfrastructure</c> work in Aspire.
/// </para>
/// </remarks>
internal sealed class PulumiAzureInfrastructure : IDistributedApplicationEventingSubscriber
{
    private readonly PulumiAzureEnvironmentResource _environment;
    private readonly ILogger<PulumiAzureInfrastructure> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PulumiAzureInfrastructure"/> class.
    /// </summary>
    public PulumiAzureInfrastructure(
        PulumiAzureEnvironmentResource environment,
        ILogger<PulumiAzureInfrastructure> logger)
    {
        _environment = environment;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task SubscribeAsync(
        IDistributedApplicationEventing eventing,
        DistributedApplicationExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        eventing.Subscribe<BeforeStartEvent>(OnBeforeStartAsync);
        return Task.CompletedTask;
    }

    private Task OnBeforeStartAsync(BeforeStartEvent @event, CancellationToken cancellationToken)
    {
        _logger.LogDebug(
            "Processing compute resources for Pulumi Azure environment '{EnvironmentName}'",
            _environment.Name);

        // Process each compute resource and create deployment targets
        foreach (var resource in @event.Model.GetComputeResources())
        {
            if (resource is not IComputeResource computeResource)
            {
                _logger.LogWarning(
                    "Resource '{ResourceName}' returned by GetComputeResources is not IComputeResource",
                    resource.Name);
                continue;
            }

            _logger.LogDebug(
                "Creating deployment target for compute resource '{ResourceName}'",
                computeResource.Name);

            // Create the provisioning resource for this compute resource
            var provisioningResource = new PulumiProvisioningResource(
                $"{computeResource.Name}-pulumi",
                computeResource,
                _environment);

            // Attach the deployment target annotation (same as Azure Container Apps does)
            computeResource.Annotations.Add(new DeploymentTargetAnnotation(provisioningResource)
            {
                ComputeEnvironment = _environment,
                ContainerRegistry = _environment.ContainerRegistry
            });

            _logger.LogDebug(
                "Attached DeploymentTargetAnnotation for '{ResourceName}' -> '{TargetName}'",
                computeResource.Name,
                provisioningResource.Name);
        }

        _logger.LogInformation(
            "Processed {Count} compute resources for Pulumi Azure environment '{EnvironmentName}'",
            @event.Model.GetComputeResources().Count(),
            _environment.Name);

        return Task.CompletedTask;
    }
}
