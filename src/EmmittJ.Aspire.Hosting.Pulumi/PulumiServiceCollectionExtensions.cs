// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EmmittJ.Aspire.Hosting.Pulumi;

/// <summary>
/// Extension methods for registering Pulumi services with dependency injection.
/// </summary>
public static class PulumiServiceCollectionExtensions
{
    /// <summary>
    /// Adds Pulumi hosting services to the service collection.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// Registers the following services:
    /// <list type="bullet">
    ///   <item><see cref="PulumiEngineContext"/> - Provides context about the Pulumi engine environment.</item>
    ///   <item><see cref="PulumiRunner"/> - Executes Pulumi programs via engine mode or Automation API.</item>
    /// </list>
    /// </remarks>
    public static IServiceCollection AddPulumiHosting(this IServiceCollection services)
    {
        // PulumiEngineContext reads from IConfiguration (environment variables)
        services.TryAddSingleton<PulumiEngineContext>();

        // PulumiRunner depends on PulumiEngineContext
        services.TryAddSingleton<PulumiRunner>();

        return services;
    }
}
