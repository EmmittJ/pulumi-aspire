// Licensed under the MIT License.

using Aspire.Hosting.ApplicationModel;
using EmmittJ.Aspire.Hosting.Pulumi;

// Extension methods that operate on IResourceBuilder live in the Aspire.Hosting namespace so they are
// discoverable without an extra using, matching the official Aspire integrations. The resource types remain
// in the EmmittJ.Aspire.Hosting.Pulumi package namespace.
namespace Aspire.Hosting;

/// <summary>
/// Extension methods shared by all Pulumi-managed compute environments.
/// </summary>
public static class PulumiEnvironmentExtensions
{
    /// <summary>
    /// Overrides the Pulumi stack name for the environment, bypassing the deploy-time Aspire environment default.
    /// </summary>
    /// <typeparam name="T">The Pulumi environment resource type.</typeparam>
    /// <param name="builder">The environment resource builder.</param>
    /// <param name="stackName">The explicit Pulumi stack name.</param>
    /// <returns>The same builder for chaining.</returns>
    /// <remarks>
    /// By default the stack is the Aspire environment selected with <c>aspire deploy --environment &lt;name&gt;</c>,
    /// mirroring how <c>Aspire.Hosting.Kubernetes</c> derives its Helm release name. Use this escape hatch when the
    /// Pulumi stack must be decoupled from the Aspire environment name.
    /// </remarks>
    public static IResourceBuilder<T> WithStackName<T>(this IResourceBuilder<T> builder, string stackName)
        where T : PulumiEnvironmentResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(stackName);

        builder.Resource.StackNameOverride = stackName;
        return builder;
    }
}
