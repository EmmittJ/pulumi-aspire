// Licensed under the MIT License.

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using EmmittJ.Aspire.Hosting.Pulumi.Azure.AppContainers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EmmittJ.Aspire.Hosting.Pulumi.Tests;

public class PulumiAzureEnvironmentTests
{
    [Fact]
    public void DefaultLocation_IsEastUs()
    {
        var resource = new PulumiAzureContainerAppEnvironmentResource("myapp");

        Assert.Equal("eastus", resource.Location);
    }

    [Fact]
    public void Location_FlowsToRegistry()
    {
        var resource = new PulumiAzureContainerAppEnvironmentResource("myapp");

        resource.Location = "westus2";

        Assert.Equal("westus2", resource.Location);
        Assert.Equal("westus2", resource.Registry.Location);
    }

    [Theory]
    [InlineData("my app")]   // space
    [InlineData("my/app")]   // slash
    [InlineData("my:app")]   // colon
    public void InvalidPulumiProjectName_Throws(string projectName)
    {
        // Pulumi project/stack names may only contain alphanumeric characters, hyphens, underscores, or periods.
        var ex = Assert.Throws<ArgumentException>(() => new PulumiAzureContainerAppEnvironmentResource("myapp", projectName));
        Assert.Equal("projectName", ex.ParamName);
    }

    [Theory]
    [InlineData("my-app")]
    [InlineData("my_app")]
    [InlineData("My.App")]
    public void ValidPulumiProjectName_IsAccepted(string projectName)
    {
        var resource = new PulumiAzureContainerAppEnvironmentResource("myapp", projectName);

        Assert.Equal(projectName, resource.PulumiProjectName);
    }

    [Fact]
    public void StackName_BeforeResolution_Throws()
    {
        var resource = new PulumiAzureContainerAppEnvironmentResource("myapp");

        // The stack is the Aspire environment, resolved at deploy time; reading it before then is invalid.
        Assert.Throws<InvalidOperationException>(() => resource.StackName);
    }

    [Fact]
    public void ResolveStackName_UsesEnvironmentName()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish", "--environment", "Staging"]);
        var environment = builder.AddPulumiAzureContainerAppEnvironment("myapp");

        using var app = builder.Build();

        // The Pulumi stack maps to the (lower-cased) Aspire environment name.
        Assert.Equal("staging", environment.Resource.ResolveStackName(app.Services));
    }

    [Fact]
    public void WithStackName_OverridesEnvironmentDefault()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish", "--environment", "Staging"]);
        var environment = builder.AddPulumiAzureContainerAppEnvironment("myapp")
            .WithStackName("prod-eu");

        using var app = builder.Build();

        // The explicit override wins over the Aspire environment name.
        Assert.Equal("prod-eu", environment.Resource.ResolveStackName(app.Services));
    }

    [Fact]
    public void RunMode_DoesNotSurfaceEnvironmentOrRegistryAsResources()
    {
        var builder = DistributedApplication.CreateBuilder();

        builder.AddPulumiAzureContainerAppEnvironment("myapp");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        // The environment and its registry must not appear in the run-mode model (no dashboard leak).
        Assert.Empty(model.Resources.OfType<PulumiAzureContainerAppEnvironmentResource>());
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
