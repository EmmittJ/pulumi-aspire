// Licensed under the MIT License.

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.Provisioning;
using Aspire.Hosting.Azure.Provisioning.Internal;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EmmittJ.Aspire.Hosting.Pulumi.InternalSeamSpike.Tests;

/// <summary>
/// 🧪 Internal-seam reuse spike: run the default Azure Container Apps environment completely UNMODIFIED —
/// no step suppression, no spliced steps, no translation — and make Pulumi the execution engine by
/// swapping the internal DI seams the native steps resolve at execution time.
/// See docs/spikes/internal-seam-reuse.md.
/// </summary>
public class InternalSeamReuseTests
{
    /// <summary>
    /// Pins the two facts the whole approach rests on:
    /// (1) this assembly can compile against Aspire.Hosting.Azure internals (IVT + public signing), and
    /// (2) every seam is registered via TryAddSingleton, so Services.Replace swaps it cleanly.
    /// </summary>
    [Fact]
    public void AllSeams_AreRegistered_AndReplaceable()
    {
        var builder = CreatePublishBuilder(out _);
        builder.AddAzureContainerAppEnvironment("acaenv");

        foreach (var seam in (Type[])
                 [
                     typeof(ITokenCredentialProvider),
                     typeof(IAcrLoginService),
                     typeof(IUserPrincipalProvider),
                     typeof(IArmClientProvider),
                     typeof(IProvisioningContextProvider),
                     typeof(IBicepProvisioner),
                     typeof(IBicepCompiler),
                 ])
        {
            var descriptor = builder.Services.LastOrDefault(d => d.ServiceType == seam);
            Assert.NotNull(descriptor);
            Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        }
    }

    /// <summary>
    /// The end-to-end proof: the full native pipeline (prepare → publish → login → provisioning context →
    /// per-resource provision → ACR login → deploy summary) executes offline with zero native steps
    /// suppressed, and the swapped <see cref="IBicepProvisioner"/> observes every template with parameters
    /// resolved by Aspire's own <c>BicepUtilities</c> — including the deployment target whose parameters
    /// chain onto the environment outputs our seam back-propagated.
    /// </summary>
    [Fact]
    public async Task NativeAcaPipeline_RunsUnsuppressed_WithPulumiSeamProvisioner()
    {
        var builder = CreatePublishBuilder(out _);

        builder.AddAzureContainerAppEnvironment("acaenv");
        builder.AddContainer("web", "nginx", "latest");

        var provisioner = new RecordingPulumiSeamProvisioner();
        var armClients = new OfflineAzureSeams.SpikeArmClientProvider();
        var acrLogins = new OfflineAzureSeams.SpikeAcrLoginService();
        ReplaceSeams(builder, provisioner, armClients, acrLogins);

        using var app = builder.Build();

        // The real `aspire deploy` phases the DAG: before-start runs to completion first (attaching the
        // deployment targets), then push (ACR login + image push), then the deploy slice. The in-process
        // harness mirrors that with filtered executions (running the whole DAG twice instead would re-run
        // the non-idempotent native prepare/publish steps).
        await ExecutePipelineAsync(app, step: "before-start");
        await ExecutePipelineAsync(app, step: "push");
        await ExecutePipelineAsync(app, step: "deploy");

        // create-provisioning-context ran for real against the offline ARM graph.
        Assert.Contains("spike-rg", armClients.ResourceGroups.Keys);

        // Every template the native environment materializes flowed through the Pulumi seam:
        // the environment (incl. ACR + Log Analytics) and the compute deployment target.
        var provisionedNames = provisioner.Provisioned.Select(p => p.Name).ToList();
        Assert.Contains("acaenv", provisionedNames);
        Assert.Contains("web-containerapp", provisionedNames);

        // Aspire's own BicepUtilities resolved the deployment target's parameters, and the
        // BicepOutputReference chain onto the environment resolved from the outputs the seam
        // back-propagated — no PulumiValueResolver, no translation layer involved.
        var target = provisioner.Provisioned.Last(p => p.Name == "web-containerapp");
        var parameterJson = target.Parameters.ToJsonString();
        Assert.Contains("/providers/Microsoft.Spike/acaenv/", parameterJson);

        // The environment template exposed outputs (registry endpoint, environment id, ...) that the
        // seam back-propagated into resource.Outputs.
        var environment = provisioner.Provisioned.Last(p => p.Name == "acaenv");
        Assert.NotEmpty(environment.Outputs);

        // The native login-to-acr step ran for real against the swapped IAcrLoginService, fed by the
        // registry endpoint output the seam produced for the environment's ACR resource.
        Assert.NotEmpty(acrLogins.Logins);
        Assert.All(acrLogins.Logins, login => Assert.Contains("/providers/Microsoft.Spike/", login));
    }

    /// <summary>Swaps every execution seam; the entire "integration" is these six Replace calls.</summary>
    private static void ReplaceSeams(
        IDistributedApplicationBuilder builder,
        IBicepProvisioner provisioner,
        IArmClientProvider armClientProvider,
        IAcrLoginService acrLoginService)
    {
        builder.Services.Replace(ServiceDescriptor.Singleton<ITokenCredentialProvider>(new OfflineAzureSeams.SpikeTokenCredentialProvider()));
        builder.Services.Replace(ServiceDescriptor.Singleton(acrLoginService));
        builder.Services.Replace(ServiceDescriptor.Singleton<IUserPrincipalProvider>(new OfflineAzureSeams.SpikeUserPrincipalProvider()));
        builder.Services.Replace(ServiceDescriptor.Singleton(armClientProvider));
        builder.Services.Replace(ServiceDescriptor.Singleton(provisioner));

        // The bicep CLI is not needed: the Pulumi seam consumes the model, not compiled ARM JSON.
        builder.Services.Replace(ServiceDescriptor.Singleton<IBicepCompiler>(new NoBicepCliCompiler()));
    }

    private sealed class NoBicepCliCompiler : IBicepCompiler
    {
        public Task<string> CompileBicepToArmAsync(string bicepFilePath, CancellationToken cancellationToken = default) =>
            Task.FromResult("{}");
    }

    /// <summary>Publish-mode builder that can execute the pipeline in a sandbox (no DCP, no cloud CLIs).</summary>
    private static IDistributedApplicationBuilder CreatePublishBuilder(out string outputPath)
    {
        outputPath = Path.Combine(Path.GetTempPath(), $"internal-seam-spike-{Guid.NewGuid():N}");
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish", "--output-path", outputPath]);

        builder.Configuration["DcpPublisher:CliPath"] = OperatingSystem.IsWindows() ? "cmd.exe" : "/usr/bin/true";
        builder.Configuration["DcpPublisher:DashboardPath"] = OperatingSystem.IsWindows() ? "cmd.exe" : "/usr/bin/true";

        // The native create-provisioning-context step reads these instead of prompting interactively.
        builder.Configuration["Azure:SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        builder.Configuration["Azure:Location"] = "westus2";
        builder.Configuration["Azure:ResourceGroup"] = "spike-rg";
        builder.Configuration["Azure:AllowResourceGroupCreation"] = "true";

        builder.Services.Configure<PipelineOptions>(options => options.SkipConfirmation = true);
        return builder;
    }

    /// <summary>Executes the full pipeline in-process.</summary>
    private static async Task ExecutePipelineAsync(DistributedApplication app, string? step = null)
    {
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var executionContext = app.Services.GetRequiredService<DistributedApplicationExecutionContext>();
        var pipeline = app.Services.GetRequiredService<IDistributedApplicationPipeline>();

        // FilterStepsForExecution reads IOptions<PipelineOptions>.Value.Step on every execution,
        // so mutating the singleton between runs selects which slice of the DAG runs.
        app.Services.GetRequiredService<IOptions<PipelineOptions>>().Value.Step = step;

        var context = new PipelineContext(
            model,
            executionContext,
            app.Services,
            NullLogger.Instance,
            CancellationToken.None);

        await pipeline.ExecuteAsync(context);
    }
}
