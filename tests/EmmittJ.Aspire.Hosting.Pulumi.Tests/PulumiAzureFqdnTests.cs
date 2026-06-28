// Licensed under the Apache License, Version 2.0.

using EmmittJ.Aspire.Hosting.Pulumi.Azure;
using Xunit;

namespace EmmittJ.Aspire.Hosting.Pulumi.Tests;

public class PulumiAzureFqdnTests
{
    private const string Domain = "happy-tree-1234.eastus.azurecontainerapps.io";

    [Fact]
    public void ExternalHttpIngress_UsesPublicFqdn()
    {
        var host = PulumiAzureComputeResourceContext.BuildFqdnHost("frontend", httpIngress: true, external: true, Domain);

        Assert.Equal($"frontend.{Domain}", host);
    }

    [Fact]
    public void InternalHttpIngress_UsesInternalFqdn()
    {
        var host = PulumiAzureComputeResourceContext.BuildFqdnHost("api", httpIngress: true, external: false, Domain);

        Assert.Equal($"api.internal.{Domain}", host);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NonHttpEndpoint_UsesInternalDnsName(bool external)
    {
        // TCP endpoints are reachable by the app's internal DNS name within the environment; the managed
        // environment domain does not apply.
        var host = PulumiAzureComputeResourceContext.BuildFqdnHost("cache", httpIngress: false, external, Domain);

        Assert.Equal("cache", host);
    }
}
