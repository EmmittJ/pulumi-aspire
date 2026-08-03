// Licensed under the MIT License.

#pragma warning disable ASPIREPIPELINES001 // Pipeline APIs are experimental
#pragma warning disable ASPIREPIPELINES002 // Pipeline tag APIs are experimental
#pragma warning disable ASPIRECOMPUTE001  // Compute resource APIs are experimental

using System.Text.Json.Nodes;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Pipelines;
using EmmittJ.Aspire.Hosting.Pulumi.Azure;
using EmmittJ.Aspire.Hosting.Pulumi.Azure.Seams;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pulumi;
using Pulumi.Testing;
using Xunit;

namespace EmmittJ.Aspire.Hosting.Pulumi.Tests;

/// <summary>
/// Pins the Bicep→azure-native translation core end-to-end on the model the native Azure Container Apps
/// environment materializes: the native prepare steps run in-process (the <c>before-start</c> pipeline
/// slice, no cloud CLI), the resolved <see cref="AzureTemplateDeployment"/>s are built the way the seam
/// builds them in production, and the translation runs under the Pulumi mock engine. An Aspire version
/// bump that changes what the environment emits fails here with a readable diff instead of
/// mistranslating at deploy time.
/// </summary>
public class PulumiAzureTranslationTests
{
    private const string SubscriptionId = "12345678-1234-1234-1234-123456789012";
    private const string PrincipalId = "11111111-2222-3333-4444-555555555555";

    [Fact]
    public async Task TranslateAcaModel_PinsTheResourceSetAndBackPropagatesOutputs()
    {
        var (app, resources, environment, target, outputPath) = await MaterializeAcaModelAsync();
        using (app)
        {
            var context = CreateDeploymentContext();
            var deployments = new List<AzureTemplateDeployment>();
            foreach (var resource in resources.Append(target))
            {
                deployments.Add(new AzureTemplateDeployment(resource, await ResolveParametersAsync(resource), [], context));
            }

            var mocks = new RecordingMocks();
            IReadOnlyDictionary<AzureBicepResource, TranslatedAzureTemplate>? templates = null;
            await Deployment.TestAsync(
                mocks,
                new TestOptions { IsPreview = false, ProjectName = "translation-test", StackName = "test" },
                async () =>
                {
                    // Mirrors PulumiTemplateProvisioner.BuildProgramAsync: one shared context, templates
                    // translated in provisioning order so earlier templates' outputs wire into later
                    // templates' parameters in-memory.
                    var translation = CreateTranslationContext();
                    foreach (var deployment in deployments)
                    {
                        await AzureProvisioningTemplateTranslator.TranslateAsync(translation, deployment);
                    }

                    templates = translation.Templates;
                });

            // The pinned resource set the ACA environment + container target translate to. No resource
            // group: the native create-provisioning-context step owns it.
            var tokens = mocks.Resources.Select(static r => r.Type).ToHashSet();
            Assert.DoesNotContain("azure-native:resources:ResourceGroup", tokens);
            Assert.Contains("azure-native:containerregistry:Registry", tokens);
            Assert.Contains("azure-native:managedidentity:UserAssignedIdentity", tokens);
            Assert.Contains("azure-native:authorization:RoleAssignment", tokens);
            Assert.Contains("azure-native:operationalinsights:Workspace", tokens);
            Assert.Contains("azure-native:app:ManagedEnvironment", tokens);
            Assert.Contains("azure-native:app:ContainerApp", tokens);

            // The container app keeps its Aspire resource name and targets the provisioning context's
            // resource group; its explicit image resolved through the Aspire-resolved parameters.
            var containerApp = Assert.Single(mocks.Resources, static r => r.Type == "azure-native:app:ContainerApp");
            Assert.Equal("web", containerApp.Inputs["containerAppName"]);
            Assert.Equal("test-rg", containerApp.Inputs["resourceGroupName"]);
            Assert.True(ContainsValue(containerApp.Inputs, "nginx:latest"), "expected the container image in the container app inputs");

            // Awaiting the translated outputs (the caller exports them as stack outputs in production)
            // forces the back-propagation applies: the Aspire model fills exactly as after a native ARM
            // deployment, releasing the provisioning gate downstream steps await.
            Assert.NotNull(templates);
            foreach (var template in templates!.Values)
            {
                foreach (var output in template.Outputs.Values)
                {
                    await global::Pulumi.Utilities.OutputUtilities.GetValueAsync(output);
                }
            }

            Assert.NotEmpty(environment.Outputs);
            Assert.NotNull(environment.ProvisioningTaskCompletionSource);
            Assert.True(environment.ProvisioningTaskCompletionSource!.Task.IsCompletedSuccessfully);

            CleanUp(outputPath);
        }
    }

    [Fact]
    public async Task TranslateAcaModel_ScopeOverride_TargetsTheScopedResourceGroup()
    {
        var (app, resources, _, target, outputPath) = await MaterializeAcaModelAsync();
        using (app)
        {
            var context = CreateDeploymentContext();
            var deployments = new List<AzureTemplateDeployment>();
            foreach (var resource in resources.Append(target))
            {
                deployments.Add(new AzureTemplateDeployment(
                    resource,
                    await ResolveParametersAsync(resource),
                    new JsonObject { ["resourceGroup"] = "scoped-rg" },
                    context));
            }

            var mocks = new RecordingMocks();
            await Deployment.TestAsync(
                mocks,
                new TestOptions { IsPreview = false, ProjectName = "translation-scope-test", StackName = "test" },
                async () =>
                {
                    var translation = CreateTranslationContext();
                    foreach (var deployment in deployments)
                    {
                        await AzureProvisioningTemplateTranslator.TranslateAsync(translation, deployment);
                    }
                });

            // The scope's resource group wins over the provisioning context's, exactly as in a native deploy.
            var registry = Assert.Single(mocks.Resources, static r => r.Type == "azure-native:containerregistry:Registry");
            Assert.Equal("scoped-rg", registry.Inputs["resourceGroupName"]);

            CleanUp(outputPath);
        }
    }

    [Fact]
    public async Task Translate_PlainBicepTemplate_ThrowsActionable()
    {
        var resource = new AzureBicepResource("plain", templateString: "param x string");
        var deployment = new AzureTemplateDeployment(resource, [], [], CreateDeploymentContext());

        var exception = await Assert.ThrowsAsync<AzureProvisioningTranslationException>(
            () => AzureProvisioningTemplateTranslator.TranslateAsync(CreateTranslationContext(), deployment));
        Assert.Contains("plain", exception.Message);
        Assert.Contains("AzureProvisioningResource", exception.Message);
    }

    /// <summary>
    /// Runs the native publish pipeline's <c>before-start</c> slice in-process — the same slice
    /// <c>aspire deploy</c> phases first — so the ACA environment's prepare steps materialize the
    /// provisioning model and attach the Bicep-backed deployment targets, with no cloud CLIs involved.
    /// Returns every materialized template in provisioning order (dependencies before dependents, the
    /// order the native provision steps hand them to the seam).
    /// </summary>
    private static async Task<(DistributedApplication App, IReadOnlyList<AzureBicepResource> Resources, AzureBicepResource Environment, AzureBicepResource Target, string OutputPath)> MaterializeAcaModelAsync()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"pulumi-azure-translation-{Guid.NewGuid():N}");
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish", "--output-path", outputPath]);
        builder.Configuration["DcpPublisher:CliPath"] = OperatingSystem.IsWindows() ? "cmd.exe" : "/usr/bin/true";
        builder.Configuration["DcpPublisher:DashboardPath"] = OperatingSystem.IsWindows() ? "cmd.exe" : "/usr/bin/true";
        builder.Services.Configure<PipelineOptions>(static options => options.SkipConfirmation = true);

        var environment = builder.AddAzureContainerAppEnvironment("acaenv");
        var container = builder.AddContainer("web", "nginx", "latest").WithHttpEndpoint(targetPort: 80);

        var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var executionContext = app.Services.GetRequiredService<DistributedApplicationExecutionContext>();
        var pipeline = app.Services.GetRequiredService<IDistributedApplicationPipeline>();

        // FilterStepsForExecution reads IOptions<PipelineOptions>.Value.Step, so setting it selects the
        // before-start slice of the DAG (running the whole DAG here would also run push/deploy steps that
        // need docker and Azure credentials).
        app.Services.GetRequiredService<IOptions<PipelineOptions>>().Value.Step = "before-start";
        await pipeline.ExecuteAsync(new PipelineContext(
            model, executionContext, app.Services, NullLogger.Instance, CancellationToken.None));

        var annotation = container.Resource.Annotations.OfType<DeploymentTargetAnnotation>().Last();
        var target = Assert.IsAssignableFrom<AzureBicepResource>(annotation.DeploymentTarget);
        var ordered = OrderByProvisioningDependencies(model.Resources.OfType<AzureBicepResource>().Where(r => r != target));
        return (app, ordered, (AzureBicepResource)environment.Resource, target, outputPath);
    }

    /// <summary>Orders templates so every <see cref="BicepOutputReference"/> points at an earlier one,
    /// mirroring the native pipeline's provision-step dependency ordering.</summary>
    private static IReadOnlyList<AzureBicepResource> OrderByProvisioningDependencies(IEnumerable<AzureBicepResource> resources)
    {
        var remaining = resources.ToList();
        var ordered = new List<AzureBicepResource>();
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(r => r.Parameters.Values
                    .OfType<BicepOutputReference>()
                    .All(reference => !remaining.Contains(reference.Resource)))
                .ToList();
            Assert.NotEmpty(ready);
            ordered.AddRange(ready);
            remaining.RemoveAll(ready.Contains);
        }

        return ordered;
    }

    private static AzureDeploymentContext CreateDeploymentContext() => new(
        SubscriptionId,
        "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
        "test-rg",
        "westus2",
        PrincipalId,
        "test@example.dev");

    private static AzureTranslationContext CreateTranslationContext() => new(
        Output.Create(SubscriptionId),
        Output.Create("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
        Output.Create("test-rg"),
        Output.Create("westus2"),
        Output.Create(PrincipalId),
        Output.Create("test@example.dev"),
        NullLogger.Instance);

    /// <summary>
    /// Builds the ARM-format resolved parameters the seam hands the provisioner in production (where
    /// Aspire's own internal <c>BicepUtilities</c> does this). <see cref="BicepOutputReference"/>s stay
    /// null: the translator wires them in-memory from the previously translated template, which is the
    /// production fast path.
    /// </summary>
    private static async Task<JsonObject> ResolveParametersAsync(AzureBicepResource resource)
    {
        var parameters = new JsonObject();
        foreach (var (name, value) in resource.Parameters)
        {
            parameters[name] = new JsonObject { ["value"] = await ResolveValueAsync(value) };
        }

        return parameters;
    }

    private static async Task<JsonNode?> ResolveValueAsync(object? value) => value switch
    {
        null => null,
        string s => JsonValue.Create(s),
        int i => JsonValue.Create(i),
        bool b => JsonValue.Create(b),
        Guid g => JsonValue.Create(g.ToString()),
        BicepOutputReference => null,
        IValueProvider provider => await provider.GetValueAsync() is { } resolved ? JsonValue.Create(resolved) : null,
        _ => JsonValue.Create(value.ToString()),
    };

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

    /// <summary>
    /// A recording mock engine: registered resources echo their inputs as state (adding a resolved ARM
    /// name and id), and invokes return one merged bag covering every state property the ACA translation
    /// reads.
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
                state = state.Add("name", args.Name!);
            }

            if (!state.ContainsKey("id"))
            {
                // The azure-native provider always surfaces the ARM resource id as an output.
                state = state.Add("id", $"/subscriptions/{SubscriptionId}/resourceGroups/test-rg/providers/mock/{args.Name}");
            }

            return Task.FromResult<(string?, object)>(($"{args.Name}-id", state));
        }

        public Task<object> CallAsync(MockCallArgs args)
        {
            lock (_gate) { _calls.Add(args); }

            // One merged bag covers every invoke the ACA translation performs: resource state reads
            // (arm id, login server, principal id, default domain) and workspace shared keys.
            return Task.FromResult<object>(new Dictionary<string, object>
            {
                ["id"] = $"/subscriptions/{SubscriptionId}/resourceGroups/test-rg/providers/mock/id",
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
