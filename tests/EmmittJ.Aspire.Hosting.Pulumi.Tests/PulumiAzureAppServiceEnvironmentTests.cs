// Licensed under the MIT License.

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using EmmittJ.Aspire.Hosting.Pulumi.Azure;
using EmmittJ.Aspire.Hosting.Pulumi.Azure.AppService;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EmmittJ.Aspire.Hosting.Pulumi.Tests;

public class PulumiAzureAppServiceEnvironmentTests
{
    [Fact]
    public void DefaultLocation_IsEastUs()
    {
        var resource = new PulumiAzureAppServiceEnvironmentResource("myapp");

        Assert.Equal("eastus", resource.Location);
    }

    [Fact]
    public void Location_FlowsToRegistry()
    {
        var resource = new PulumiAzureAppServiceEnvironmentResource("myapp");

        resource.Location = "westus2";

        Assert.Equal("westus2", resource.Location);
        Assert.Equal("westus2", resource.Registry.Location);
    }

    [Fact]
    public void DefaultPlanSku_IsPremiumV3()
    {
        // P0v3 matches Aspire's Azure App Service integration: the smallest tier supporting Linux
        // custom containers with per-site scaling.
        var resource = new PulumiAzureAppServiceEnvironmentResource("myapp");

        Assert.Equal("P0v3", resource.PlanSkuName);
        Assert.Equal("Premium0V3", resource.PlanSkuTier);
    }

    [Fact]
    public void HttpsUpgrade_DefaultsToTrue()
    {
        var resource = new PulumiAzureAppServiceEnvironmentResource("myapp");

        Assert.True(resource.HttpsUpgrade);
    }

    [Theory]
    [InlineData("my app")]   // space
    [InlineData("my/app")]   // slash
    [InlineData("my:app")]   // colon
    public void InvalidPulumiProjectName_Throws(string projectName)
    {
        // Pulumi project/stack names may only contain alphanumeric characters, hyphens, underscores, or periods.
        var ex = Assert.Throws<ArgumentException>(() => new PulumiAzureAppServiceEnvironmentResource("myapp", projectName));
        Assert.Equal("projectName", ex.ParamName);
    }

    [Fact]
    public void StackName_BeforeResolution_Throws()
    {
        var resource = new PulumiAzureAppServiceEnvironmentResource("myapp");

        // The stack is the Aspire environment, resolved at deploy time; reading it before then is invalid.
        Assert.Throws<InvalidOperationException>(() => resource.StackName);
    }

    [Fact]
    public void ResolveStackName_UsesEnvironmentName()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish", "--environment", "Staging"]);
        var environment = builder.AddPulumiAzureAppServiceEnvironment("myapp");

        using var app = builder.Build();

        // The Pulumi stack maps to the (lower-cased) Aspire environment name.
        Assert.Equal("staging", environment.Resource.ResolveStackName(app.Services));
    }

    [Fact]
    public void WithStackName_OverridesEnvironmentDefault()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish", "--environment", "Staging"]);
        var environment = builder.AddPulumiAzureAppServiceEnvironment("myapp")
            .WithStackName("prod-eu");

        using var app = builder.Build();

        // The explicit override wins over the Aspire environment name.
        Assert.Equal("prod-eu", environment.Resource.ResolveStackName(app.Services));
    }

    [Fact]
    public void WithPlanSku_ConfiguresPlan()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        var environment = builder.AddPulumiAzureAppServiceEnvironment("myapp")
            .WithPlanSku("P1v3", "Premium0V3");

        Assert.Equal("P1v3", environment.Resource.PlanSkuName);
        Assert.Equal("Premium0V3", environment.Resource.PlanSkuTier);
    }

    [Fact]
    public void WithHttpsUpgrade_False_DisablesUpgrade()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        var environment = builder.AddPulumiAzureAppServiceEnvironment("myapp")
            .WithHttpsUpgrade(false);

        Assert.False(environment.Resource.HttpsUpgrade);
    }

    [Fact]
    public void RunMode_DoesNotSurfaceEnvironmentOrRegistryAsResources()
    {
        var builder = DistributedApplication.CreateBuilder();

        builder.AddPulumiAzureAppServiceEnvironment("myapp");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        // The environment and its registry must not appear in the run-mode model (no dashboard leak).
        Assert.Empty(model.Resources.OfType<PulumiAzureAppServiceEnvironmentResource>());
        Assert.Empty(model.Resources.OfType<PulumiAzureContainerRegistryResource>());
    }

    [Fact]
    public void PublishAsPulumiAppServiceWebsite_InRunMode_DoesNotAttachAnnotation()
    {
        var builder = DistributedApplication.CreateBuilder();

        var container = builder.AddContainer("api", "nginx")
            .PublishAsPulumiAppServiceWebsite((_, _) => { });

        // PublishAs{Target} customizations only apply to publish/deploy output; in run mode the builder is
        // returned unchanged so the annotation is never attached.
        Assert.False(container.Resource.HasAnnotationOfType<PulumiAzureAppServiceWebsiteCustomizationAnnotation>());
    }

    [Fact]
    public void PublishAsPulumiAppServiceWebsite_InPublishMode_AttachesAnnotation()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);

        var container = builder.AddContainer("api", "nginx")
            .PublishAsPulumiAppServiceWebsite((_, _) => { });

        Assert.True(container.Resource.HasAnnotationOfType<PulumiAzureAppServiceWebsiteCustomizationAnnotation>());
    }
}
