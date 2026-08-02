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
/// inferred from the adopted environment's type (falling back to structural classification for unknown
/// Azure compute environments), the translation program is defaulted, and the registry-first phase is on
/// by default.
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
    public void ResolveSelector_UsesPinnedSelectorsForKnownTypes_FallsBackToStructural()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        var aca = builder.AddAzureContainerAppEnvironment("aca-env");
        var appService = builder.AddAzureAppServiceEnvironment("appsvc-env");

        // Known environments get their belt-and-braces selectors (structural classification plus the
        // catalogued names/tags); anything else adopts with structural classification alone, so a new
        // Azure compute environment works without a library update.
        Assert.Same(
            PulumiStepSuppressionSelector.AzureContainerApps,
            PulumiAzureEnvironmentExtensions.ResolveSelector(aca.Resource));
        Assert.Same(
            PulumiStepSuppressionSelector.AzureAppService,
            PulumiAzureEnvironmentExtensions.ResolveSelector(appService.Resource));
        Assert.Same(
            PulumiStepSuppressionSelector.Structural,
            PulumiAzureEnvironmentExtensions.ResolveSelector(new UnknownAzureComputeEnvironmentResource("custom-env")));
    }

    [Fact]
    public void PublishAsPulumi_UnknownAzureEnvironment_AdoptsWithStructuralClassification()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        var environment = builder.AddResource(new UnknownAzureComputeEnvironmentResource("custom-env"));

        environment.PublishAsPulumi();

        var pulumiEnvironment = Assert.Single(builder.Resources.OfType<PulumiEnvironmentResource>());
        Assert.Equal("custom-env-pulumi", pulumiEnvironment.Name);
        Assert.Same(environment.Resource, pulumiEnvironment.AdoptedEnvironment);
    }

    [Fact]
    public void PublishAsPulumi_UnknownAzureEnvironmentInRunMode_IsNoOp()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var environment = builder.AddResource(new UnknownAzureComputeEnvironmentResource("custom-env"));

        // Run mode stays a no-op: local development is untouched.
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
