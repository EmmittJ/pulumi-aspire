// Licensed under the MIT License.

using Microsoft.Extensions.Hosting;

namespace EmmittJ.Aspire.Hosting.Pulumi.Azure;

/// <summary>
/// Resolves the Pulumi project and stack names shared by the deploy and destroy paths, so
/// <c>aspire destroy</c> always targets exactly the stack <c>aspire deploy</c> deployed.
/// </summary>
internal static class PulumiStackNameResolver
{
    /// <summary>Resolves the Pulumi project name: the explicit option when set, otherwise the AppHost's
    /// application name sanitized to Pulumi's allowed character set.</summary>
    public static string ResolveProjectName(PulumiProvisioningOptions options, IHostEnvironment hostEnvironment)
    {
        if (options.ProjectName is { } explicitName)
        {
            return PulumiNaming.ValidateName(explicitName, nameof(PulumiProvisioningOptions.ProjectName));
        }

        var sanitized = string.Concat(hostEnvironment.ApplicationName.Select(
            static c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-'));
        return PulumiNaming.ValidateName(sanitized, nameof(PulumiProvisioningOptions.ProjectName));
    }

    /// <summary>Resolves the Pulumi stack name: the explicit option when set, otherwise the AppHost's
    /// environment name, lower-cased.</summary>
    public static string ResolveStackName(PulumiProvisioningOptions options, IHostEnvironment hostEnvironment) =>
        PulumiNaming.ValidateName(
            options.StackName ?? hostEnvironment.EnvironmentName.ToLowerInvariant(),
            nameof(PulumiProvisioningOptions.StackName));
}
