// Licensed under the MIT License.

using System.Text.RegularExpressions;

namespace EmmittJ.Aspire.Hosting.Pulumi;

/// <summary>
/// Helpers for validating Pulumi project and stack names.
/// </summary>
internal static partial class PulumiNaming
{
    // Pulumi project and stack names may only contain alphanumeric characters, hyphens, underscores, or
    // periods. See:
    // - https://www.pulumi.com/docs/iac/concepts/stacks/ ("Stack names may only contain alphanumeric
    //   characters, hyphens, underscores, or periods.")
    // - https://www.pulumi.com/docs/iac/concepts/projects/project-file/ (project "name" attribute).
    [GeneratedRegex(@"^[a-zA-Z0-9._-]+$")]
    private static partial Regex NamePattern();

    /// <summary>
    /// Validates that a Pulumi project or stack name conforms to Pulumi's allowed character set.
    /// </summary>
    /// <param name="value">The name to validate.</param>
    /// <param name="paramName">The parameter name reported in the exception.</param>
    /// <returns>The validated name.</returns>
    /// <exception cref="ArgumentException">Thrown when the name is empty or contains unsupported characters.</exception>
    public static string ValidateName(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrEmpty(value, paramName);

        if (!NamePattern().IsMatch(value))
        {
            throw new ArgumentException(
                $"'{value}' is not a valid Pulumi project or stack name. Names may only contain alphanumeric " +
                "characters, hyphens, underscores, or periods.",
                paramName);
        }

        return value;
    }
}
