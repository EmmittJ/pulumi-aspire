// Licensed under the MIT License.

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EmmittJ.Aspire.Hosting.Pulumi.Tests;

public class PulumiValueResolverTests
{
    [Fact]
    public async Task SecretParameter_ResolvesAsSecret_NonSecretAndStrings_DoNot()
    {
        using var app = BuildApp(out var executionContext);
        var resolver = new PulumiValueResolver(executionContext);

        var secretParameter = new ParameterResource("password", _ => "p@ss", secret: true);
        var plainParameter = new ParameterResource("region", _ => "eastus", secret: false);

        // Secret parameters must be flagged secret (wrapped as Pulumi secrets), never inlined as plaintext.
        Assert.True((await resolver.ResolveAsync(secretParameter)).IsSecret);
        Assert.False((await resolver.ResolveAsync(plainParameter)).IsSecret);
        Assert.False((await resolver.ResolveAsync("literal")).IsSecret);
    }

    [Fact]
    public async Task ReferenceExpression_WithSecretConstituent_IsSecret()
    {
        using var app = BuildApp(out var executionContext);
        var resolver = new PulumiValueResolver(executionContext);

        var secretParameter = new ParameterResource("password", _ => "p@ss", secret: true);
        var composite = ReferenceExpression.Create($"conn-{secretParameter}");

        // If any constituent value is secret, the whole composite must be secret.
        Assert.True((await resolver.ResolveAsync(composite)).IsSecret);
    }

    [Fact]
    public async Task EndpointReference_WithoutResolverHook_Throws()
    {
        using var app = BuildApp(out var executionContext);
        var builder = DistributedApplication.CreateBuilder();
        var container = builder.AddContainer("web", "nginx:latest").WithHttpEndpoint(targetPort: 80);
        var resolver = new PulumiValueResolver(executionContext);

        // Endpoint resolution is platform-specific; without a hook the resolver must fail actionably.
        await Assert.ThrowsAsync<NotSupportedException>(
            () => resolver.ResolveAsync(container.Resource.GetEndpoint("http")));
    }

    private static DistributedApplication BuildApp(out DistributedApplicationExecutionContext executionContext)
    {
        var builder = DistributedApplication.CreateBuilder();
        var app = builder.Build();

        executionContext = app.Services.GetRequiredService<DistributedApplicationExecutionContext>();
        return app;
    }
}
