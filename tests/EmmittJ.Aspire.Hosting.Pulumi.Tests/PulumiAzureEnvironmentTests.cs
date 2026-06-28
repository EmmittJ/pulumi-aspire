// Licensed under the MIT License.

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using EmmittJ.Aspire.Hosting.Pulumi.Azure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EmmittJ.Aspire.Hosting.Pulumi.Tests;

public class PulumiAzureEnvironmentTests
{
    [Fact]
    public void DefaultLocation_IsEastUs()
    {
        var resource = new PulumiAzureEnvironmentResource("test");

        Assert.Equal("eastus", resource.Location);
    }

    [Fact]
    public void Location_FlowsToRegistry()
    {
        var resource = new PulumiAzureEnvironmentResource("test");

        resource.Location = "westus2";

        Assert.Equal("westus2", resource.Location);
        Assert.Equal("westus2", resource.Registry.Location);
    }

    [Fact]
    public void RunMode_DoesNotSurfaceEnvironmentOrRegistryAsResources()
    {
        var builder = DistributedApplication.CreateBuilder();

        builder.AddPulumiAzureEnvironment("dev");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        // The environment and its registry must not appear in the run-mode model (no dashboard leak).
        Assert.Empty(model.Resources.OfType<PulumiAzureEnvironmentResource>());
        Assert.Empty(model.Resources.OfType<PulumiAzureContainerRegistryResource>());
    }

    [Fact]
    public void PublishAsPulumiContainerApp_InRunMode_DoesNotAttachAnnotation()
    {
        var builder = DistributedApplication.CreateBuilder();

        var container = builder.AddContainer("api", "nginx")
            .PublishAsPulumiContainerApp((_, _) => { });

        // PublishAs{Target} customizations only apply to publish/deploy output; in run mode the builder is
        // returned unchanged so the annotation is never attached.
        Assert.False(container.Resource.HasAnnotationOfType<PulumiAzureContainerAppCustomizationAnnotation>());
    }
}
