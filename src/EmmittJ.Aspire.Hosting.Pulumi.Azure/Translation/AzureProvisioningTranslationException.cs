// Licensed under the MIT License.

namespace EmmittJ.Aspire.Hosting.Pulumi.Azure;

/// <summary>
/// Thrown when the Azure provisioning model cannot be translated to Pulumi azure-native resources. The
/// message always names the resource, the property path, and the offending construct so translation gaps
/// are actionable instead of silent.
/// </summary>
public sealed class AzureProvisioningTranslationException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="AzureProvisioningTranslationException"/> class.</summary>
    /// <param name="message">The actionable error message.</param>
    public AzureProvisioningTranslationException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AzureProvisioningTranslationException"/> class.</summary>
    /// <param name="message">The actionable error message.</param>
    /// <param name="innerException">The underlying failure.</param>
    public AzureProvisioningTranslationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal static AzureProvisioningTranslationException ForConstruct(
        string templateName,
        string constructName,
        string propertyPath,
        string detail) =>
        new($"Cannot translate template '{templateName}', construct '{constructName}', property '{propertyPath}': {detail}");
}
