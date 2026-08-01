// Licensed under the MIT License.
// SCRATCH SPIKE FILE - not committed.

using System.Runtime.CompilerServices;
using System.Text;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Pipelines;
using Azure.Provisioning;
using Azure.Provisioning.Primitives;
using Xunit;

namespace EmmittJ.Aspire.Hosting.Pulumi.NativeAdoptionSpike.Tests;

public class ScratchConstructGraphDump
{
    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    private static extern AzureResourceInfrastructure CreateInfrastructure(AzureProvisioningResource resource, string name);

    private static AzureResourceInfrastructure BuildInfrastructure(AzureProvisioningResource resource)
    {
        var infra = CreateInfrastructure(resource, resource.Name);
        resource.ConfigureInfrastructure(infra);

        // Replicate AzureProvisioningResource.EnsureParametersAlign.
        var existing = infra.GetProvisionableResources().OfType<ProvisioningParameter>()
            .DistinctBy(p => p.BicepIdentifier).ToDictionary(p => p.BicepIdentifier);
        foreach (var parameter in resource.Parameters)
        {
            if (!existing.ContainsKey(parameter.Key))
            {
                var isSecure = parameter.Value is ParameterResource { Secret: true };
                infra.Add(new ProvisioningParameter(parameter.Key, typeof(string)) { IsSecure = isSecure });
            }
        }

        return infra;
    }

    private static void DumpInfra(StringBuilder sb, AzureProvisioningResource resource)
    {
        sb.AppendLine($"########## {resource.Name} ({resource.GetType().Name})");
        sb.AppendLine($"  Parameters dict: {string.Join(", ", resource.Parameters.Select(kvp => $"{kvp.Key}={kvp.Value?.GetType().Name ?? "null"}"))}");
        sb.AppendLine($"  Outputs dict: {string.Join(", ", resource.Outputs.Keys)}");
        sb.AppendLine($"  Scope: {resource.Scope?.ResourceGroup}");

        var infra = BuildInfrastructure(resource);
        var plan = infra.Build(resource.ProvisioningBuildOptions);
        foreach (var provisionable in infra.GetProvisionableResources())
        {
            switch (provisionable)
            {
                case ProvisioningParameter p:
                    sb.AppendLine($"  PARAM {p.BicepIdentifier} secure={p.IsSecure} type={p.BicepType} value={SafeCompile(p.Value)}");
                    break;
                case ProvisioningOutput o:
                    sb.AppendLine($"  OUTPUT {o.BicepIdentifier} type={o.BicepType} value={SafeCompile(o.Value)}");
                    break;
                case ProvisioningVariable v:
                    sb.AppendLine($"  VAR {v.BicepIdentifier} value={SafeCompile(v.Value)}");
                    break;
                case ProvisionableResource r:
                    sb.AppendLine($"  RESOURCE {r.BicepIdentifier} type={r.ResourceType} version={r.ResourceVersion} existing={r.IsExistingResource} dependsOn=[{string.Join(",", r.DependsOn.Select(d => d.BicepIdentifier))}]");
                    foreach (var (name, value) in r.ProvisionableProperties)
                    {
                        if (value.Kind == BicepValueKind.Unset && !value.IsOutput)
                        {
                            continue;
                        }

                        sb.AppendLine($"    PROP {name} path=[{string.Join(".", value.Self?.BicepPath ?? [])}] kind={value.Kind} secure={value.IsSecure} out={value.IsOutput} compiled={SafeCompile(value)}");
                    }

                    break;
                default:
                    sb.AppendLine($"  OTHER {provisionable.GetType().FullName}");
                    break;
            }
        }

        // Faithfulness check: does the captured graph compile to the same bicep Aspire produces?
        var compiled = plan.Compile().First().Value;
        var official = resource.GetBicepTemplateString();
        sb.AppendLine($"  FAITHFUL={(compiled == official)}");
        if (compiled != official)
        {
            sb.AppendLine("---- captured ----").AppendLine(compiled).AppendLine("---- official ----").AppendLine(official);
        }
    }

    private static string SafeCompile(IBicepValue value)
    {
        try
        {
            return value.Compile().ToString() ?? "<null>";
        }
        catch (Exception ex)
        {
            return $"<compile-error: {ex.Message}>";
        }
    }

    [Fact]
    public async Task DumpAcaConstructGraph()
    {
        var builder = PipelineSpikeHarness.CreatePublishBuilder(out var outputPath);
        builder.AddAzureContainerAppEnvironment("aca-env");
        builder.AddContainer("web", "nginx:latest").WithHttpEndpoint(targetPort: 80);

        PipelineSpikeHarness.WrapNativeSteps(
            builder,
            _ => true,
            step => PulumiStepSuppressionSelector.AzureContainerApps.Matches(step)
                || step.Tags.Contains(WellKnownPipelineTags.BuildCompute)
                || step.Tags.Contains(WellKnownPipelineTags.PushContainerImage)
                || step.Name.StartsWith("publish-azure", StringComparison.Ordinal)
                || step.Name.StartsWith("print-dashboard-url-", StringComparison.Ordinal));

        var sb = new StringBuilder();
        builder.Pipeline.AddStep(
            "dump-construct-graph",
            context =>
            {
                foreach (var resource in context.Model.Resources)
                {
                    if (resource is AzureProvisioningResource provisioning)
                    {
                        DumpInfra(sb, provisioning);
                    }
                    else if (resource is AzureBicepResource bicep)
                    {
                        sb.AppendLine($"########## {resource.Name} ({resource.GetType().Name}) -- plain bicep, NOT provisioning");
                    }
                    else
                    {
                        sb.AppendLine($"########## {resource.Name} ({resource.GetType().Name}) -- not bicep");
                    }

                    if (resource.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var annotation))
                    {
                        sb.AppendLine($">>> deployment target of {resource.Name}: {annotation.DeploymentTarget.Name} ({annotation.DeploymentTarget.GetType().Name}), registry={annotation.ContainerRegistry?.GetType().Name}");
                        if (annotation.DeploymentTarget is AzureProvisioningResource targetProvisioning)
                        {
                            DumpInfra(sb, targetProvisioning);
                        }
                    }
                }

                return Task.CompletedTask;
            },
            dependsOn: (string[])[WellKnownPipelineSteps.Push, WellKnownPipelineSteps.BeforeStart],
            requiredBy: WellKnownPipelineSteps.Deploy);

        using var app = builder.Build();
        await PipelineSpikeHarness.ExecutePipelineAsync(app);

        File.WriteAllText("/tmp/spike-dump-aca.txt", sb.ToString());

        if (Directory.Exists(outputPath))
        {
            Directory.Delete(outputPath, recursive: true);
        }
    }

    [Fact]
    public async Task DumpRicherGraphs()
    {
        var builder = PipelineSpikeHarness.CreatePublishBuilder(out var outputPath);
        builder.Configuration["Parameters:api-key"] = "s3cret";
        builder.AddAzureContainerAppEnvironment("aca-env");
        var secret = builder.AddParameter("api-key", secret: true);
        var api = builder.AddContainer("api", "myimage:1.0")
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints()
            .WithEnvironment("API_KEY", secret);
        builder.AddContainer("web", "nginx:latest")
            .WithDockerfile("/tmp/dockerctx") // forces image build+push -> containerimage parameter
            .WithHttpEndpoint(targetPort: 80)
            .WithEnvironment("API_URL", api.GetEndpoint("http"))
            .WithReference(api.GetEndpoint("http"));

        PipelineSpikeHarness.WrapNativeSteps(
            builder,
            _ => true,
            step => PulumiStepSuppressionSelector.AzureContainerApps.Matches(step)
                || step.Tags.Contains(WellKnownPipelineTags.BuildCompute)
                || step.Tags.Contains(WellKnownPipelineTags.PushContainerImage)
                || step.Name.StartsWith("publish-azure", StringComparison.Ordinal)
                || step.Name.StartsWith("print-dashboard-url-", StringComparison.Ordinal));

        var sb = new StringBuilder();
        builder.Pipeline.AddStep(
            "dump-construct-graph-2",
            context =>
            {
                foreach (var resource in context.Model.Resources)
                {
                    if (resource.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var annotation)
                        && annotation.DeploymentTarget is AzureProvisioningResource targetProvisioning)
                    {
                        sb.AppendLine($">>> target of {resource.Name}");
                        DumpInfra(sb, targetProvisioning);
                    }
                }

                return Task.CompletedTask;
            },
            dependsOn: (string[])[WellKnownPipelineSteps.Push, WellKnownPipelineSteps.BeforeStart],
            requiredBy: WellKnownPipelineSteps.Deploy);

        using var app = builder.Build();
        await PipelineSpikeHarness.ExecutePipelineAsync(app);

        File.WriteAllText("/tmp/spike-dump-aca-rich.txt", sb.ToString());

        if (Directory.Exists(outputPath))
        {
            Directory.Delete(outputPath, recursive: true);
        }
    }

    [Fact]
    public async Task DumpAppServiceGraph()
    {
        var builder = PipelineSpikeHarness.CreatePublishBuilder(out var outputPath);
        builder.AddAzureAppServiceEnvironment("appsvc-env");
        builder.AddContainer("web", "nginx:latest")
            .WithDockerfile("/tmp/dockerctx")
            .WithHttpEndpoint(targetPort: 80)
            .WithExternalHttpEndpoints();

        PipelineSpikeHarness.WrapNativeSteps(
            builder,
            _ => true,
            step => PulumiStepSuppressionSelector.AzureAppService.Matches(step)
                || step.Tags.Contains(WellKnownPipelineTags.BuildCompute)
                || step.Tags.Contains(WellKnownPipelineTags.PushContainerImage)
                || step.Name.StartsWith("publish-azure", StringComparison.Ordinal)
                || step.Name.StartsWith("print-dashboard-url-", StringComparison.Ordinal));

        var sb = new StringBuilder();
        builder.Pipeline.AddStep(
            "dump-construct-graph-3",
            context =>
            {
                foreach (var resource in context.Model.Resources)
                {
                    if (resource is AzureProvisioningResource provisioning)
                    {
                        DumpInfra(sb, provisioning);
                    }

                    if (resource.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var annotation)
                        && annotation.DeploymentTarget is AzureProvisioningResource targetProvisioning)
                    {
                        sb.AppendLine($">>> target of {resource.Name}");
                        DumpInfra(sb, targetProvisioning);
                    }
                }

                return Task.CompletedTask;
            },
            dependsOn: (string[])[WellKnownPipelineSteps.Push, WellKnownPipelineSteps.BeforeStart],
            requiredBy: WellKnownPipelineSteps.Deploy);

        // App Service race workaround from the spike: bridge before-start -> push-prereq.
        builder.Pipeline.AddStep(
            "spike-bridge",
            _ => Task.CompletedTask,
            dependsOn: WellKnownPipelineSteps.BeforeStart,
            requiredBy: "push-prereq");

        using var app = builder.Build();
        await PipelineSpikeHarness.ExecutePipelineAsync(app);

        File.WriteAllText("/tmp/spike-dump-appsvc.txt", sb.ToString());

        if (Directory.Exists(outputPath))
        {
            Directory.Delete(outputPath, recursive: true);
        }
    }
}
