// Licensed under the MIT License.

namespace EmmittJ.Aspire.Hosting.Pulumi.Azure;

/// <summary>
/// Options for translating an adopted native Azure compute environment into Pulumi azure-native resources
/// (see <c>TranslateAzureEnvironmentAsync</c>).
/// </summary>
public sealed class AzureAdoptionOptions
{
    /// <summary>
    /// Gets or sets the name of the resource group every translated resource targets. Defaults to
    /// <c>{adopted-environment-name}-rg</c>.
    /// </summary>
    public string? ResourceGroupName { get; set; }

    /// <summary>
    /// Gets or sets the Azure location for the resource group. Falls back to the
    /// <c>azure-native:location</c> Pulumi config value. Required when deploying a new resource group;
    /// publish previews substitute a deterministic placeholder when unset.
    /// </summary>
    public string? Location { get; set; }

    /// <summary>
    /// Gets or sets whether to target an existing resource group instead of creating one. When set, the
    /// resource group named <see cref="ResourceGroupName"/> must already exist; its location is read from
    /// Azure at deploy time unless <see cref="Location"/> is provided.
    /// </summary>
    public bool UseExistingResourceGroup { get; set; }

    /// <summary>
    /// Gets or sets a per-resource customization hook invoked just before each translated resource is
    /// created, for Pulumi-only concerns such as providers, aliases, protect flags, or property overrides.
    /// </summary>
    public Action<AzureNativeResourceCustomizationContext>? ConfigureResource { get; set; }
}
