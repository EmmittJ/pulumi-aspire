// Licensed under the MIT License.

#pragma warning disable ASPIRECOMPUTE001  // Compute resource APIs are experimental
#pragma warning disable ASPIRECOMPUTE002  // IComputeEnvironmentResource is experimental
#pragma warning disable ASPIRECOMPUTE003  // IContainerRegistry is experimental

using Aspire.Hosting.ApplicationModel;

namespace EmmittJ.Aspire.Hosting.Pulumi.Tests;

/// <summary>A minimal resource used by tests that only need an <see cref="IResource"/> with a name.</summary>
internal sealed class TestResource(string name) : Resource(name);

/// <summary>A minimal native compute environment stand-in for adoption tests.</summary>
internal sealed class TestComputeEnvironmentResource(string name) : Resource(name), IComputeEnvironmentResource;

/// <summary>A minimal container registry stand-in for registry-phase and structural-selector tests.</summary>
internal sealed class TestContainerRegistryResource(string name) : Resource(name), IContainerRegistry
{
    ReferenceExpression IContainerRegistry.Name => ReferenceExpression.Create($"{Name}");
    ReferenceExpression IContainerRegistry.Endpoint => ReferenceExpression.Create($"{Name}.example.io");
}
