// Licensed under the Apache License, Version 2.0.

#pragma warning disable ASPIREPIPELINES001 // Pipeline APIs are experimental

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EmmittJ.Aspire.Hosting.Pulumi;

/// <summary>
/// Registration helpers for the Pulumi hosting integration. Provider packages call
/// <see cref="AddPulumiInfrastructureCore(IDistributedApplicationBuilder)"/> from their environment <c>Add*</c> method.
/// </summary>
public static class PulumiInfrastructureExtensions
{
    /// <summary>
    /// Registers the shared Pulumi services and the single global validation step idempotently.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <returns>The same builder for chaining.</returns>
    /// <remarks>
    /// Safe to call multiple times (for example when adding more than one Pulumi environment). The
    /// <see cref="PulumiRunner"/> and the validation step are only registered once via a marker singleton.
    /// </remarks>
    public static IDistributedApplicationBuilder AddPulumiInfrastructureCore(this IDistributedApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton<PulumiRunner>();

        if (builder.Services.All(d => d.ServiceType != typeof(PulumiPipelineStepMarker)))
        {
            builder.Services.AddSingleton<PulumiPipelineStepMarker>();

            // One global validation step: fail clearly if a resource carries a Pulumi customization but there
            // is no Pulumi environment to honor it. Only meaningful in publish mode.
            builder.Pipeline.AddStep(
                name: PulumiPipelineStepMarker.StepName,
                action: static context =>
                {
                    if (!context.ExecutionContext.IsPublishMode)
                    {
                        return Task.CompletedTask;
                    }

                    var hasPulumiEnvironment = context.Model.Resources.OfType<PulumiEnvironmentResource>().Any();
                    if (hasPulumiEnvironment)
                    {
                        return Task.CompletedTask;
                    }

                    foreach (var resource in context.Model.Resources)
                    {
                        if (resource.HasAnnotationOfType<PulumiCustomizationAnnotation>())
                        {
                            throw new InvalidOperationException(
                                $"Resource '{resource.Name}' is configured with a Pulumi customization, but no Pulumi " +
                                $"environment has been added. Add one with the provider's 'AddPulumi...Environment' method.");
                        }
                    }

                    return Task.CompletedTask;
                },
                requiredBy: WellKnownPipelineSteps.BeforeStart);
        }

        return builder;
    }

    private sealed class PulumiPipelineStepMarker
    {
        public const string StepName = "validate-pulumi";
    }
}
