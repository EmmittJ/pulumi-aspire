// Licensed under the MIT License.

using EmmittJ.Aspire.Hosting.Pulumi.Azure.Seams;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pulumi;

namespace EmmittJ.Aspire.Hosting.Pulumi.Azure;

/// <summary>
/// The Pulumi execution engine behind Aspire's native Azure provisioning pipeline: every template the
/// pipeline asks to provision (environment resources first, then each compute resource's deployment
/// target) is appended to a cumulative model, and one <c>pulumi up</c> runs against a single stack whose
/// inline program translates the whole model so far. Pulumi's diffing makes each successive up
/// incremental — already-deployed resources no-op — so the stack always reflects the full application
/// and remains independently usable (<c>pulumi preview</c>, <c>pulumi destroy</c>, drift detection).
/// </summary>
internal sealed class PulumiTemplateProvisioner : IAzureTemplateProvisioner
{
    private readonly PulumiProvisioningOptions _options;
    private readonly PulumiRunner _runner;
    private readonly IHostEnvironment _hostEnvironment;
    private readonly ILogger<PulumiTemplateProvisioner> _logger;

    // Provision steps for independent templates run concurrently in the native pipeline, but ups against
    // one stack must serialize.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<AzureTemplateDeployment> _deployments = [];

    public PulumiTemplateProvisioner(PulumiProvisioningOptions options, IServiceProvider serviceProvider)
    {
        _options = options;
        _runner = serviceProvider.GetRequiredService<PulumiRunner>();
        _hostEnvironment = serviceProvider.GetRequiredService<IHostEnvironment>();
        _logger = serviceProvider.GetRequiredService<ILogger<PulumiTemplateProvisioner>>();
    }

    public async Task ProvisionAsync(AzureTemplateDeployment deployment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deployment);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _deployments.Add(deployment);

            var projectName = ResolveProjectName();
            var stackName = ResolveStackName();
            _logger.LogInformation(
                "Deploying template '{Template}' with Pulumi (project '{Project}', stack '{Stack}', {Count} template(s) in the program).",
                deployment.Resource.Name, projectName, stackName, _deployments.Count);

            var result = await _runner
                .ForStack(projectName, stackName)
                .WithWorkDir(_options.WorkingDirectory)
                .UpAsync(BuildProgramAsync, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Pulumi up for template '{Template}' completed: {Result}.",
                deployment.Resource.Name, result.Summary.Result);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The inline Pulumi program: translates every template deployed so far, in provisioning order, and
    /// exports each template's outputs as <c>{template}_{output}</c> stack outputs. Exporting the outputs
    /// is load-bearing — it roots the applies that back-propagate deployed values into Aspire's model
    /// (<c>AzureBicepResource.Outputs</c> + provisioning gate), which downstream native steps (registry
    /// login, deployment-target parameter resolution) consume.
    /// </summary>
    internal async Task<IDictionary<string, object?>> BuildProgramAsync()
    {
        var current = _deployments[^1].Context;
        var context = new AzureTranslationContext(
            Output.Create(current.SubscriptionId),
            Output.Create(current.TenantId ?? string.Empty),
            Output.Create(current.ResourceGroupName),
            Output.Create(current.Location),
            Output.Create(current.PrincipalId),
            Output.Create(current.PrincipalName),
            _logger)
        {
            ConfigureResource = _options.ConfigureResource,
        };

        var stackOutputs = new Dictionary<string, object?>();
        foreach (var deployment in _deployments)
        {
            var translated = await AzureProvisioningTemplateTranslator.TranslateAsync(context, deployment).ConfigureAwait(false);
            foreach (var (name, value) in translated.Outputs)
            {
                stackOutputs[$"{translated.Name}_{name}"] = value;
            }
        }

        return stackOutputs;
    }

    private string ResolveProjectName()
    {
        if (_options.ProjectName is { } explicitName)
        {
            return PulumiNaming.ValidateName(explicitName, nameof(PulumiProvisioningOptions.ProjectName));
        }

        var sanitized = string.Concat(_hostEnvironment.ApplicationName.Select(
            static c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-'));
        return PulumiNaming.ValidateName(sanitized, nameof(PulumiProvisioningOptions.ProjectName));
    }

    private string ResolveStackName() => PulumiNaming.ValidateName(
        _options.StackName ?? _hostEnvironment.EnvironmentName.ToLowerInvariant(),
        nameof(PulumiProvisioningOptions.StackName));
}
