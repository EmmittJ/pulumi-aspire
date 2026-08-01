// Licensed under the MIT License.

namespace EmmittJ.Aspire.Hosting.Pulumi;

/// <summary>
/// The Pulumi operation a program run is part of. Adoption programs use this to adapt their behavior —
/// most importantly, publish previews must produce an artifact without cloud credentials, so translation
/// layers substitute deterministic placeholders for ambient invokes during <see cref="Preview"/>.
/// </summary>
public enum PulumiOperation
{
    /// <summary>A <c>pulumi preview</c> run producing a reviewable artifact (the publish step).</summary>
    Preview,

    /// <summary>A <c>pulumi up</c> run provisioning real resources (the deploy step).</summary>
    Up,

    /// <summary>A <c>pulumi destroy</c> run tearing down the stack (the destroy step).</summary>
    Destroy,
}
