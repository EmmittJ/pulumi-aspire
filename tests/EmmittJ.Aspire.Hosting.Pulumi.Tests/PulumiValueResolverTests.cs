// Licensed under the Apache License, Version 2.0.

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using EmmittJ.Aspire.Hosting.Pulumi.Azure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pulumi;
using Xunit;
using PulumiResource = Pulumi.Resource;

namespace EmmittJ.Aspire.Hosting.Pulumi.Tests;

public class PulumiValueResolverTests
{
    [Fact]
    public async Task SecretParameter_ResolvesAsSecret_NonSecretAndStrings_DoNot()
    {
        using var app = BuildApp(out var context);
        var resolver = new TestComputeResourceContext(new TestComputeResource("api"), context);

        var secretParameter = new ParameterResource("password", _ => "p@ss", secret: true);
        var plainParameter = new ParameterResource("region", _ => "eastus", secret: false);

        // Secret parameters must be flagged secret (wrapped as Pulumi secrets), never inlined as plaintext.
        Assert.True((await resolver.Resolve(secretParameter)).IsSecret);
        Assert.False((await resolver.Resolve(plainParameter)).IsSecret);
        Assert.False((await resolver.Resolve("literal")).IsSecret);
    }

    private static DistributedApplication BuildApp(out PulumiPublishingContext context)
    {
        var builder = DistributedApplication.CreateBuilder();
        var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var executionContext = app.Services.GetRequiredService<DistributedApplicationExecutionContext>();

        context = new PulumiPublishingContext(
            model,
            new PulumiAzureEnvironmentResource("dev"),
            executionContext,
            app.Services,
            NullLogger.Instance,
            CancellationToken.None);

        return app;
    }

    private sealed class TestComputeResource(string name) : global::Aspire.Hosting.ApplicationModel.Resource(name), IComputeResource;

    private sealed class TestComputeResourceContext(IComputeResource resource, PulumiPublishingContext context)
        : PulumiComputeResourceContext(resource, context)
    {
        public Task<PulumiResolvedValue> Resolve(object? value) => ResolveValueAsync(value);

        protected override Task<PulumiResource> BuildComputeResourceAsync() => throw new NotSupportedException();

        protected override Output<string> ResolveEndpoint(EndpointReference endpoint) => Output.Create(string.Empty);

        protected override Output<string> ResolveEndpointExpression(EndpointReferenceExpression expression) => Output.Create(string.Empty);
    }
}
