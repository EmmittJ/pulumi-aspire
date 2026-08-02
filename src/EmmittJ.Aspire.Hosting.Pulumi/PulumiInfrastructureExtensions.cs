// Licensed under the MIT License.

using Aspire.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EmmittJ.Aspire.Hosting.Pulumi;

/// <summary>
/// Registration helpers for the Pulumi hosting integration. The <c>PublishAsPulumi</c> decorator calls
/// <see cref="AddPulumiInfrastructureCore(IDistributedApplicationBuilder)"/> before registering the
/// Pulumi environment resource.
/// </summary>
public static class PulumiInfrastructureExtensions
{
    /// <summary>
    /// Registers the shared Pulumi services idempotently.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <returns>The same builder for chaining.</returns>
    /// <remarks>
    /// Safe to call multiple times (for example when adopting more than one environment). The
    /// <see cref="PulumiRunner"/> is only registered once.
    /// </remarks>
    public static IDistributedApplicationBuilder AddPulumiInfrastructureCore(this IDistributedApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton<PulumiRunner>();

        return builder;
    }
}
