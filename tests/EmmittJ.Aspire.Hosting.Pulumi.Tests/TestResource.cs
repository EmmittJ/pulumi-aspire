// Licensed under the MIT License.

using Aspire.Hosting.ApplicationModel;

namespace EmmittJ.Aspire.Hosting.Pulumi.Tests;

/// <summary>A minimal resource used by tests that only need an <see cref="IResource"/> with a name.</summary>
internal sealed class TestResource(string name) : Resource(name);
