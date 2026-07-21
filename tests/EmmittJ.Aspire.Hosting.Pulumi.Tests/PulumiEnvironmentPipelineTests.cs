// Licensed under the MIT License.

#pragma warning disable ASPIREPIPELINES001 // Pipeline APIs are experimental

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using EmmittJ.Aspire.Hosting.Pulumi.Azure.AppContainers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EmmittJ.Aspire.Hosting.Pulumi.Tests;

public class PulumiEnvironmentPipelineTests
{
    [Fact]
    public async Task EnvironmentResource_RegistersExpectedLifecycleSteps()
    {
        var steps = await ResolveEnvironmentStepsAsync("app");

        var prepare = steps.Single(s => s.Name == "pulumi-prepare-app");
        Assert.Contains(WellKnownPipelineSteps.ValidateComputeEnvironments, prepare.DependsOnSteps);
        Assert.Contains(WellKnownPipelineSteps.BeforeStart, prepare.RequiredBySteps);

        var publish = steps.Single(s => s.Name == "pulumi-publish-app");
        Assert.Contains(WellKnownPipelineSteps.PublishPrereq, publish.DependsOnSteps);
        Assert.Contains(WellKnownPipelineSteps.Publish, publish.RequiredBySteps);

        var deploy = steps.Single(s => s.Name == "pulumi-deploy-app");
        // Deploy must run after images are pushed so Container Apps reference real registry tags.
        Assert.Contains(WellKnownPipelineSteps.Push, deploy.DependsOnSteps);
        Assert.Contains(WellKnownPipelineSteps.Deploy, deploy.RequiredBySteps);
    }

    [Fact]
    public async Task EnvironmentResource_DestroyStep_DependsOnDestroyPrereq()
    {
        var steps = await ResolveEnvironmentStepsAsync("app");

        var destroy = steps.Single(s => s.Name == "pulumi-destroy-app");
        Assert.Contains(WellKnownPipelineSteps.DestroyPrereq, destroy.DependsOnSteps);
        Assert.Contains(WellKnownPipelineSteps.Destroy, destroy.RequiredBySteps);
    }

    [Fact]
    public async Task RegistryResource_HasProvisionAndDestroySteps()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        var environment = builder.AddPulumiAzureContainerAppEnvironment("app");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var executionContext = app.Services.GetRequiredService<DistributedApplicationExecutionContext>();

        var registry = model.Resources.OfType<PulumiAzureContainerRegistryResource>().Single();

        var pipelineContext = new PipelineContext(
            model,
            executionContext,
            app.Services,
            NullLogger.Instance,
            CancellationToken.None);

        var factoryContext = new PipelineStepFactoryContext
        {
            PipelineContext = pipelineContext,
            Resource = registry,
        };

        var annotation = registry.Annotations.OfType<PipelineStepAnnotation>().Single();
        var steps = (await annotation.CreateStepsAsync(factoryContext)).ToList();

        // The registry is its own Pulumi stack, so it must provision before push and destroy on teardown,
        // otherwise `aspire destroy` would orphan the registry and its resource group.
        var provision = steps.Single(s => s.Name == $"pulumi-deploy-registry-{registry.Name}");
        Assert.Contains(WellKnownPipelineSteps.PushPrereq, provision.RequiredBySteps);

        var destroy = steps.Single(s => s.Name == $"pulumi-destroy-registry-{registry.Name}");
        Assert.Contains(WellKnownPipelineSteps.DestroyPrereq, destroy.DependsOnSteps);
        Assert.Contains(WellKnownPipelineSteps.Destroy, destroy.RequiredBySteps);
    }

    private static async Task<List<PipelineStep>> ResolveEnvironmentStepsAsync(string name)
    {
        // Publish mode is required for the environment (and registry) to be added to the model.
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        var environment = builder.AddPulumiAzureContainerAppEnvironment(name);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var executionContext = app.Services.GetRequiredService<DistributedApplicationExecutionContext>();
        Assert.True(executionContext.IsPublishMode);

        var pipelineContext = new PipelineContext(
            model,
            executionContext,
            app.Services,
            NullLogger.Instance,
            CancellationToken.None);

        var factoryContext = new PipelineStepFactoryContext
        {
            PipelineContext = pipelineContext,
            Resource = environment.Resource,
        };

        var annotation = environment.Resource.Annotations.OfType<PipelineStepAnnotation>().Single();
        var steps = await annotation.CreateStepsAsync(factoryContext);
        return [.. steps];
    }
}
