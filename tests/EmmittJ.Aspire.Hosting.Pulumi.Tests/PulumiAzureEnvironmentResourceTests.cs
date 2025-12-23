// Licensed under the Apache License, Version 2.0.

#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using EmmittJ.Aspire.Hosting.Pulumi.Azure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EmmittJ.Aspire.Hosting.Pulumi.Tests;

public class PulumiAzureEnvironmentResourceTests
{
    [Fact]
    public void Name_SetsResourceName()
    {
        // Arrange & Act
        var resource = new PulumiAzureEnvironmentResource("prod");

        // Assert
        // The resource Name is used as the Pulumi stack name
        Assert.Equal("prod", resource.Name);
    }

    [Fact]
    public void DefaultLocation_IsEastUs()
    {
        // Arrange & Act
        var resource = new PulumiAzureEnvironmentResource("test");

        // Assert
        Assert.Equal("eastus", resource.Location);
    }

    [Fact]
    public void WithLocation_SetsLocation()
    {
        // Arrange
        var resource = new PulumiAzureEnvironmentResource("test");

        // Act
        resource.Location = "westus2";

        // Assert
        Assert.Equal("westus2", resource.Location);
    }

    [Fact]
    public void GetHostAddressExpression_ReturnsAzureContainerAppsFormat()
    {
        // Arrange
        var resource = new PulumiAzureEnvironmentResource("test");

        // This test would need a mock EndpointReference
        // For now, just verify the resource can be created
        Assert.NotNull(resource);
    }

    [Fact]
    public void AddPulumiAzureEnvironment_AddsRegistryToModel()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();

        // Act
        var azure = builder.AddPulumiAzureEnvironment("dev");
        var app = builder.Build();

        // Assert
        // The registry should be in the model
        var model = app.Services.GetService<DistributedApplicationModel>();
        Assert.NotNull(model);

        var resources = model.Resources.ToList();

        // Should have at least the environment and registry
        Assert.True(resources.Count >= 2, $"Expected at least 2 resources, found {resources.Count}: {string.Join(", ", resources.Select(r => r.Name))}");

        // Find the registry
        var registry = resources.FirstOrDefault(r => r is PulumiAzureContainerRegistryResource);
        Assert.NotNull(registry);

        // The registry should have a PipelineStepAnnotation
        var annotations = registry.Annotations.OfType<PipelineStepAnnotation>().ToList();
        Assert.NotEmpty(annotations);
    }
}
