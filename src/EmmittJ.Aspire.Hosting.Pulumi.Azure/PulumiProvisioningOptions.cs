// Licensed under the MIT License.

namespace EmmittJ.Aspire.Hosting.Pulumi.Azure;

/// <summary>
/// Settings for Pulumi-backed Azure provisioning. Everything defaults sensibly: the project name comes
/// from the application name, the stack name from the environment name, and the target subscription,
/// resource group, and location come from Aspire's own provisioning context (interactive prompts,
/// <c>Azure:*</c> configuration, or deployment-state, exactly as with the default provisioner).
/// </summary>
public sealed class PulumiProvisioningOptions
{
    /// <summary>
    /// Gets or sets the Pulumi project name. Defaults to the AppHost's application name (sanitized to
    /// Pulumi's allowed character set).
    /// </summary>
    public string? ProjectName { get; set; }

    /// <summary>
    /// Gets or sets the Pulumi stack name. Defaults to the AppHost's environment name, lower-cased
    /// (for example <c>production</c>).
    /// </summary>
    public string? StackName { get; set; }

    /// <summary>
    /// Gets or sets an explicit working directory for the Pulumi Automation API workspace. When unset, a
    /// stable per-project temp directory is used (state itself lives in the configured Pulumi backend).
    /// </summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Gets or sets a hook invoked for every translated resource just before it is created, for
    /// Pulumi-only concerns such as providers, aliases, protect flags, or property overrides.
    /// </summary>
    public Action<AzureNativeResourceCustomizationContext>? ConfigureResource { get; set; }
}
