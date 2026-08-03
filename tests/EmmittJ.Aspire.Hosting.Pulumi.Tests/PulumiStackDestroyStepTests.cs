// Licensed under the MIT License.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using EmmittJ.Aspire.Hosting.Pulumi.Azure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EmmittJ.Aspire.Hosting.Pulumi.Tests;

/// <summary>
/// Pins the destroy wiring of <see cref="PulumiAzureProvisioningExtensions.UsePulumiProvisioning"/>: the
/// <c>destroy-pulumi-stack</c> step hangs off the standard destroy slots, and the pipeline configuration
/// orders the native <c>destroy-azure-*</c> resource-group deletion after it — so <c>pulumi destroy</c>
/// runs while the resources still exist and the stack's state ends up empty instead of stale.
/// </summary>
public class PulumiStackDestroyStepTests
{
    [Fact]
    public void CreateStep_HangsOffTheStandardDestroySlots()
    {
        var step = PulumiStackDestroyStep.CreateStep(new PulumiProvisioningOptions());

        Assert.Equal("destroy-pulumi-stack", step.Name);
        Assert.Contains(WellKnownPipelineSteps.DestroyPrereq, step.DependsOnSteps);
        Assert.Contains(WellKnownPipelineSteps.Destroy, step.RequiredBySteps);
    }

    [Fact]
    public async Task ConfigurePipeline_OrdersNativeAzureDestroyAfterThePulumiDestroy()
    {
        var azureDestroy = CreateNoOpStep("destroy-azure-acaenv");
        var unrelatedDestroy = CreateNoOpStep("destroy-compose-env");
        var provision = CreateNoOpStep("provision-acaenv");
        var context = new PipelineConfigurationContext
        {
            Services = new ServiceCollection().BuildServiceProvider(),
            Steps = [azureDestroy, unrelatedDestroy, provision],
            Model = new DistributedApplicationModel(Enumerable.Empty<IResource>()),
        };

        await PulumiStackDestroyStep.ConfigurePipelineAsync(context);

        // The resource-group deletion must wait for pulumi destroy; every other step is untouched.
        Assert.Contains(PulumiStackDestroyStep.StepName, azureDestroy.DependsOnSteps);
        Assert.DoesNotContain(PulumiStackDestroyStep.StepName, unrelatedDestroy.DependsOnSteps);
        Assert.DoesNotContain(PulumiStackDestroyStep.StepName, provision.DependsOnSteps);
    }

    private static PipelineStep CreateNoOpStep(string name) => new()
    {
        Name = name,
        Action = static _ => Task.CompletedTask,
    };
}
