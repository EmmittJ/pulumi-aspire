// Licensed under the MIT License.

using Aspire.Hosting;
using EmmittJ.Aspire.Hosting.Pulumi.Azure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EmmittJ.Aspire.Hosting.Pulumi.Tests;

/// <summary>
/// Pins the wiring contract of <see cref="PulumiAzureProvisioningExtensions.UsePulumiProvisioning"/>:
/// the two internal execution seams (<c>IBicepProvisioner</c>, <c>IBicepCompiler</c>) are replaced —
/// order-independently, because Aspire registers them with <c>TryAddSingleton</c> — and local
/// development stays untouched. The seam types are asserted by full name on purpose: this project has
/// no access to Aspire.Hosting.Azure internals, exactly like any consumer.
/// </summary>
public class PulumiProvisioningWiringTests
{
    private const string BicepProvisionerType = "Aspire.Hosting.Azure.Provisioning.IBicepProvisioner";
    private const string BicepCompilerType = "Aspire.Hosting.Azure.Provisioning.Internal.IBicepCompiler";

    [Fact]
    public void UsePulumiProvisioning_AfterEnvironment_ReplacesTheExecutionSeams()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish", "--output-path", Path.GetTempPath()]);
        builder.AddAzureContainerAppEnvironment("acaenv");

        builder.UsePulumiProvisioning();

        AssertSeamsReplaced(builder);
    }

    [Fact]
    public void UsePulumiProvisioning_BeforeEnvironment_ReplacesTheExecutionSeams()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish", "--output-path", Path.GetTempPath()]);

        // Replace registers the seam before Aspire's TryAddSingleton runs, which then no-ops: the
        // extension works regardless of call order.
        builder.UsePulumiProvisioning();
        builder.AddAzureContainerAppEnvironment("acaenv");

        AssertSeamsReplaced(builder);
    }

    [Fact]
    public void UsePulumiProvisioning_InRunMode_IsANoOp()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        builder.AddAzureContainerAppEnvironment("acaenv");

        builder.UsePulumiProvisioning();

        // Local development keeps the default provisioner and never registers the Pulumi runner.
        Assert.DoesNotContain(builder.Services, static d => d.ServiceType == typeof(PulumiRunner));
        var provisioner = builder.Services.LastOrDefault(static d => d.ServiceType.FullName == BicepProvisionerType);
        Assert.True(provisioner is null || provisioner.ImplementationFactory is null,
            "run mode must keep Aspire's default IBicepProvisioner registration");
    }

    private static void AssertSeamsReplaced(IDistributedApplicationBuilder builder)
    {
        var provisioner = builder.Services.LastOrDefault(static d => d.ServiceType.FullName == BicepProvisionerType);
        Assert.NotNull(provisioner);
        Assert.Equal(ServiceLifetime.Singleton, provisioner.Lifetime);
        Assert.NotNull(provisioner.ImplementationFactory);

        var compiler = builder.Services.LastOrDefault(static d => d.ServiceType.FullName == BicepCompilerType);
        Assert.NotNull(compiler);
        Assert.Contains("NoBicepCliCompiler", compiler.ImplementationInstance?.GetType().Name);

        // Exactly one registration each: Replace removed Aspire's original descriptor (or pre-empted its
        // TryAddSingleton), so the swapped seams are unambiguous.
        Assert.Single(builder.Services, static d => d.ServiceType.FullName == BicepProvisionerType);
        Assert.Single(builder.Services, static d => d.ServiceType.FullName == BicepCompilerType);

        Assert.Contains(builder.Services, static d => d.ServiceType == typeof(PulumiRunner));
    }
}
