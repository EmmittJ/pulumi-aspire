// Licensed under the MIT License.

#pragma warning disable ASPIREPIPELINES001 // Pipeline APIs are experimental
#pragma warning disable ASPIRECOMPUTE001  // Compute resource APIs are experimental
#pragma warning disable ASPIRECOMPUTE002  // IComputeEnvironmentResource is experimental

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using EmmittJ.Aspire.Hosting.Pulumi.Azure;
using Xunit;

namespace EmmittJ.Aspire.Hosting.Pulumi.Tests;

/// <summary>
/// Tests for the one-line Azure adoption entry point
/// (<see cref="PulumiAzureEnvironmentExtensions.PublishAsPulumi{T}"/>): the suppression selector is
/// inferred from the adopted environment's type, the translation program is defaulted, and the
/// registry-first phase is on by default. The type names the inference pins are catalogue data — an Aspire
/// rename must fail here with the exact diff.
/// </summary>
public class PulumiAzurePublishAsPulumiTests
{
    [Fact]
    public void PublishAsPulumi_ContainerAppEnvironment_InfersSelectorAndDefaultsRegistryPhase()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        var environment = builder.AddAzureContainerAppEnvironment("aca-env");

        environment.PublishAsPulumi();

        var pulumiEnvironment = Assert.Single(builder.Resources.OfType<PulumiEnvironmentResource>());
        Assert.Equal("aca-env-pulumi", pulumiEnvironment.Name);
        Assert.Same(environment.Resource, pulumiEnvironment.AdoptedEnvironment);

        // The registry-first phase (Aspire's own registry model, in its own stack) is on by default,
        // including the az acr login callback that replaces the suppressed native login step.
        Assert.NotNull(pulumiEnvironment.RegistryPhase);
        Assert.NotNull(pulumiEnvironment.RegistryPhase.LoginCallback);
        Assert.Equal("aca-env-pulumi-registry", pulumiEnvironment.RegistryProjectName);
    }

    [Fact]
    public void PublishAsPulumi_AppServiceEnvironment_InfersSelectorAndDefaultsRegistryPhase()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        var environment = builder.AddAzureAppServiceEnvironment("appsvc-env");

        environment.PublishAsPulumi();

        var pulumiEnvironment = Assert.Single(builder.Resources.OfType<PulumiEnvironmentResource>());
        Assert.Equal("appsvc-env-pulumi", pulumiEnvironment.Name);
        Assert.NotNull(pulumiEnvironment.RegistryPhase);
    }

    [Fact]
    public void ResolveSelector_PinsNativeEnvironmentTypeNames()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        var aca = builder.AddAzureContainerAppEnvironment("aca-env");
        var appService = builder.AddAzureAppServiceEnvironment("appsvc-env");

        Assert.Same(
            PulumiStepSuppressionSelector.AzureContainerApps,
            PulumiAzureEnvironmentExtensions.ResolveSelector(aca.Resource));
        Assert.Same(
            PulumiStepSuppressionSelector.AzureAppService,
            PulumiAzureEnvironmentExtensions.ResolveSelector(appService.Resource));
    }

    [Fact]
    public void PublishAsPulumi_UnknownAzureEnvironment_ThrowsActionableError()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        var environment = builder.AddResource(new UnknownAzureComputeEnvironmentResource("custom-env"));

        var exception = Assert.Throws<NotSupportedException>(() => environment.PublishAsPulumi());
        Assert.Contains("custom-env", exception.Message);
        Assert.Contains("PulumiStepSuppressionSelector", exception.Message);
        Assert.Empty(builder.Resources.OfType<PulumiEnvironmentResource>());
    }

    [Fact]
    public void PublishAsPulumi_UnknownAzureEnvironmentInRunMode_IsNoOp()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var environment = builder.AddResource(new UnknownAzureComputeEnvironmentResource("custom-env"));

        // Run mode must never fail selector inference: local development stays untouched.
        environment.PublishAsPulumi();

        Assert.Empty(builder.Resources.OfType<PulumiEnvironmentResource>());
    }

    [Fact]
    public void PublishAsPulumi_InRunMode_IsNoOp()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var environment = builder.AddAzureContainerAppEnvironment("aca-env");

        environment.PublishAsPulumi();

        Assert.Empty(builder.Resources.OfType<PulumiEnvironmentResource>());
    }

    [Fact]
    public void PublishAsPulumi_ConfigureEnvironment_AppliesWithStackName()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        var environment = builder.AddAzureContainerAppEnvironment("aca-env");

        environment.PublishAsPulumi(configureEnvironment: env => env.WithStackName("prod-eu"));

        var pulumiEnvironment = Assert.Single(builder.Resources.OfType<PulumiEnvironmentResource>());
        Assert.Equal("prod-eu", pulumiEnvironment.StackNameOverride);
    }

    [Fact]
    public void PublishAsPulumi_CustomProgram_KeepsInferredSelectorAndRegistryPhase()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        var environment = builder.AddAzureContainerAppEnvironment("aca-env");

        environment.PublishAsPulumi(program: _ => Task.CompletedTask);

        var pulumiEnvironment = Assert.Single(builder.Resources.OfType<PulumiEnvironmentResource>());
        Assert.NotNull(pulumiEnvironment.RegistryPhase);
    }

    /// <summary>An Azure compute environment the selector inference does not recognize.</summary>
    private sealed class UnknownAzureComputeEnvironmentResource(string name)
        : Resource(name), IAzureComputeEnvironmentResource;
}
