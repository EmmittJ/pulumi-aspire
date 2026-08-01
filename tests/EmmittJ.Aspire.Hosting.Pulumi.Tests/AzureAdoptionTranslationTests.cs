// Licensed under the MIT License.

#pragma warning disable ASPIREPIPELINES001 // Pipeline APIs are experimental
#pragma warning disable ASPIREPIPELINES002 // Pipeline tag APIs are experimental
#pragma warning disable ASPIRECOMPUTE001  // Compute resource APIs are experimental
#pragma warning disable ASPIRECOMPUTE002  // IComputeEnvironmentResource is experimental

using System.Collections.Immutable;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Pipelines;
using EmmittJ.Aspire.Hosting.Pulumi.Azure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pulumi;
using Pulumi.Testing;
using Xunit;

namespace EmmittJ.Aspire.Hosting.Pulumi.Tests;

/// <summary>
/// Pins the Azure adoption frontend (<see cref="PulumiAzureAdoptionExtensions.TranslateAzureEnvironmentAsync"/>)
/// end-to-end: the native Azure Container Apps environment's prepare steps materialize the provisioning
/// model in-process (no cloud CLI), and the frontend translates it into azure-native resources under the
/// Pulumi mock engine. An Aspire version bump that changes what the environment emits fails here with a
/// readable diff instead of mistranslating at deploy time.
/// </summary>
public class AzureAdoptionTranslationTests
{
    [Fact]
    public async Task TranslateAzureEnvironment_Preview_PinsAcaTranslationWithoutCredentials()
    {
        var (app, adoption, outputPath) = await MaterializeAcaModelAsync(PulumiOperation.Preview);
        using (app)
        {
            var mocks = new RecordingMocks();
            await Deployment.TestAsync(
                mocks,
                new TestOptions { IsPreview = true, ProjectName = "adoption-test", StackName = "test" },
                async () => await adoption.TranslateAzureEnvironmentAsync());

            // The pinned resource set the ACA environment + container target translate to.
            var tokens = mocks.Resources.Select(static r => r.Type).ToHashSet();
            Assert.Contains("azure-native:resources:ResourceGroup", tokens);
            Assert.Contains("azure-native:containerregistry:Registry", tokens);
            Assert.Contains("azure-native:managedidentity:UserAssignedIdentity", tokens);
            Assert.Contains("azure-native:authorization:RoleAssignment", tokens);
            Assert.Contains("azure-native:operationalinsights:Workspace", tokens);
            Assert.Contains("azure-native:app:ManagedEnvironment", tokens);
            Assert.Contains("azure-native:app:ContainerApp", tokens);

            // Previews must not require Azure credentials: no ambient invokes at all.
            Assert.Empty(mocks.Calls);

            // The container app keeps its Aspire resource name and targets the frontend's resource group.
            var containerApp = Assert.Single(mocks.Resources, static r => r.Type == "azure-native:app:ContainerApp");
            Assert.Equal("web", containerApp.Inputs["containerAppName"]);
            Assert.Equal("aca-env-rg", containerApp.Inputs["resourceGroupName"]);

            // Every environment output a deployment-target parameter references is exported as a stack
            // output — this rooting is what makes the translator's back-propagation applies run.
            Assert.True(adoption.Outputs.ContainsKey("resourceGroupName"));
            var target = Assert.Single(
                adoption.GetDeploymentTargets().Select(static t => t.Target.DeploymentTarget).OfType<AzureBicepResource>());
            foreach (var reference in target.Parameters.Values.OfType<BicepOutputReference>())
            {
                Assert.True(
                    adoption.Outputs.ContainsKey($"{reference.Resource.Name}_{reference.Name}"),
                    $"expected stack output '{reference.Resource.Name}_{reference.Name}'");
            }

            CleanUp(outputPath);
        }
    }

    [Fact]
    public async Task TranslateAzureEnvironment_Up_UsesAmbientInvokesAndResolvedImages()
    {
        var (app, adoption, outputPath) = await MaterializeAcaModelAsync(PulumiOperation.Up);
        using (app)
        {
            var mocks = new RecordingMocks();
            await Deployment.TestAsync(
                mocks,
                new TestOptions { IsPreview = false, ProjectName = "adoption-test", StackName = "test" },
                async () => await adoption.TranslateAzureEnvironmentAsync(
                    new AzureAdoptionOptions { Location = "westus2" }));

            // Deploys resolve ambient values through real invokes (mocked here).
            Assert.Contains(mocks.Calls, static c => c.Token == "azure-native:authorization:getClientConfig");

            // The container's explicit image resolves through the ContainerImageReference parameter.
            var containerApp = Assert.Single(mocks.Resources, static r => r.Type == "azure-native:app:ContainerApp");
            Assert.True(ContainsValue(containerApp.Inputs, "nginx:latest"), "expected the container image in the container app inputs");

            CleanUp(outputPath);
        }
    }

    [Fact]
    public async Task TranslateAzureEnvironment_WithoutAzureModel_ThrowsActionable()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        var environment = builder.AddResource(new TestComputeEnvironmentResource("native-env"));
        using var app = builder.Build();

        var backend = new PulumiBackendResource("native-env-pulumi", environment.Resource, _ => Task.CompletedTask);
        var adoption = new PulumiAdoptionContext(
            app.Services.GetRequiredService<DistributedApplicationModel>(),
            backend,
            PulumiOperation.Preview,
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
            app.Services,
            NullLogger.Instance,
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => adoption.TranslateAzureEnvironmentAsync());
        Assert.Contains("native-env", exception.Message);
        Assert.Contains("AddAzureContainerAppEnvironment", exception.Message);
    }

    /// <summary>
    /// Runs the publish pipeline for an ACA environment + one container in-process so the native prepare
    /// steps attach the Bicep-backed deployment targets, then wraps the materialized model in a
    /// <see cref="PulumiAdoptionContext"/> for the frontend under test. Execution steps are suppressed with
    /// the production selector; build/push and azure-publish steps are neutralized because the sandbox has
    /// no docker or Azure CLI.
    /// </summary>
    private static async Task<(DistributedApplication App, PulumiAdoptionContext Adoption, string OutputPath)> MaterializeAcaModelAsync(
        PulumiOperation operation)
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"azure-adoption-translation-{Guid.NewGuid():N}");
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish", "--output-path", outputPath]);
        builder.Configuration["DcpPublisher:CliPath"] = OperatingSystem.IsWindows() ? "cmd.exe" : "/usr/bin/true";
        builder.Configuration["DcpPublisher:DashboardPath"] = OperatingSystem.IsWindows() ? "cmd.exe" : "/usr/bin/true";

        var environment = builder.AddAzureContainerAppEnvironment("aca-env");
        builder.AddContainer("web", "nginx:latest").WithHttpEndpoint(targetPort: 80);

        NativePipelineStepAdoption.SuppressExecutionSteps(
            builder,
            static resource => resource is AzureBicepResource || resource.GetType().Name == "AzureEnvironmentResource",
            PulumiStepSuppressionSelector.AzureContainerApps);

        NativePipelineStepAdoption.SuppressExecutionSteps(
            builder,
            static _ => true,
            new PulumiStepSuppressionSelector
            {
                StepNamePrefixes = ["publish-azure", "print-dashboard-url-"],
                Tags = [WellKnownPipelineTags.BuildCompute, WellKnownPipelineTags.PushContainerImage],
            });

        var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var executionContext = app.Services.GetRequiredService<DistributedApplicationExecutionContext>();
        var pipeline = app.Services.GetRequiredService<IDistributedApplicationPipeline>();
        await pipeline.ExecuteAsync(new PipelineContext(model, executionContext, app.Services, NullLogger.Instance, CancellationToken.None));

        var backend = new PulumiBackendResource("aca-env-pulumi", environment.Resource, _ => Task.CompletedTask);
        var adoption = new PulumiAdoptionContext(
            model, backend, operation, executionContext, app.Services, NullLogger.Instance, CancellationToken.None);
        return (app, adoption, outputPath);
    }

    private static void CleanUp(string outputPath)
    {
        if (Directory.Exists(outputPath))
        {
            Directory.Delete(outputPath, recursive: true);
        }
    }

    /// <summary>Recursively searches a serialized mock input tree for a string value.</summary>
    private static bool ContainsValue(object? node, string expected) => node switch
    {
        string s => s == expected,
        IReadOnlyDictionary<string, object> map => map.Values.Any(v => ContainsValue(v, expected)),
        IEnumerable<object> list => list.Any(v => ContainsValue(v, expected)),
        _ => false,
    };

    /// <summary>A minimal non-Azure compute environment for the negative-path test.</summary>
    private sealed class TestComputeEnvironmentResource(string name)
        : global::Aspire.Hosting.ApplicationModel.Resource(name), IComputeEnvironmentResource;

    /// <summary>
    /// A recording mock engine: registered resources echo their inputs as state (adding a resolved ARM
    /// name), and invokes return one merged bag covering every state property the ACA translation reads.
    /// </summary>
    private sealed class RecordingMocks : IMocks
    {
        private readonly Lock _gate = new();
        private readonly List<MockResourceArgs> _resources = [];
        private readonly List<MockCallArgs> _calls = [];

        public IReadOnlyList<MockResourceArgs> Resources
        {
            get { lock (_gate) { return [.. _resources]; } }
        }

        public IReadOnlyList<MockCallArgs> Calls
        {
            get { lock (_gate) { return [.. _calls]; } }
        }

        public Task<(string? id, object state)> NewResourceAsync(MockResourceArgs args)
        {
            lock (_gate) { _resources.Add(args); }

            var state = args.Inputs;
            if (!state.ContainsKey("name"))
            {
                // Typed resources surface the ARM name through a 'name' output (the resource group's Name).
                state = state.Add("name", state.TryGetValue("resourceGroupName", out var groupName) ? groupName : args.Name!);
            }

            if (!state.ContainsKey("id"))
            {
                // The azure-native provider always surfaces the ARM resource id as an output.
                state = state.Add("id", $"{args.Name}-id");
            }

            return Task.FromResult<(string?, object)>(($"{args.Name}-id", state));
        }

        public Task<object> CallAsync(MockCallArgs args)
        {
            lock (_gate) { _calls.Add(args); }

            // One merged bag covers every invoke the ACA translation performs: client config, resource
            // state reads (arm id, login server, principal id, default domain), and workspace shared keys.
            return Task.FromResult<object>(new Dictionary<string, object>
            {
                ["id"] = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/mock-rg/providers/mock/id",
                ["subscriptionId"] = "00000000-0000-0000-0000-000000000001",
                ["tenantId"] = "00000000-0000-0000-0000-000000000002",
                ["objectId"] = "00000000-0000-0000-0000-000000000003",
                ["clientId"] = "00000000-0000-0000-0000-000000000004",
                ["loginServer"] = "mockacr.azurecr.io",
                ["principalId"] = "00000000-0000-0000-0000-000000000005",
                ["defaultDomain"] = "mock.azurecontainerapps.io",
                ["primarySharedKey"] = "mock-shared-key",
                ["customerId"] = "00000000-0000-0000-0000-000000000006",
            });
        }
    }
}
