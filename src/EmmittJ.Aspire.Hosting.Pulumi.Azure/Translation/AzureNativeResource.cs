// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Reflection;
using Pulumi;

namespace EmmittJ.Aspire.Hosting.Pulumi.Azure;

/// <summary>
/// An untyped Pulumi <c>azure-native</c> resource: a token plus a property bag. The translation layer uses
/// this single wrapper for every ARM resource type so coverage does not depend on per-type generated
/// classes; the azure-native provider performs the ARM shape validation server-side.
/// </summary>
public sealed class AzureNativeResource : global::Pulumi.CustomResource
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AzureNativeResource"/> class.
    /// </summary>
    /// <param name="token">The azure-native resource token, for example <c>azure-native:app:ContainerApp</c>.</param>
    /// <param name="name">The Pulumi resource name (URN segment).</param>
    /// <param name="properties">The resource inputs. Values may be plain values, nested dictionaries/lists, or Pulumi outputs.</param>
    /// <param name="options">Optional resource options. The azure-native plugin version is applied automatically when unset.</param>
    public AzureNativeResource(
        string token,
        string name,
        IDictionary<string, object?> properties,
        CustomResourceOptions? options = null)
        : base(token, name, new DictionaryResourceArgs(Normalize(properties)), WithProviderVersion(options))
    {
    }

    /// <summary>
    /// Gets the azure-native provider/plugin version matching the referenced Pulumi.AzureNative SDK, used
    /// for automatic plugin acquisition on untyped resources and invokes.
    /// </summary>
    public static string ProviderVersion { get; } = ResolveProviderVersion();

    /// <summary>
    /// Deep-converts mutable dictionaries/lists in a translated property bag into the immutable forms
    /// <see cref="DictionaryResourceArgs"/> requires (only <c>ImmutableDictionary&lt;string, ...&gt;</c> and
    /// <c>ImmutableArray&lt;...&gt;</c> generics are accepted by the Pulumi serializer's type check).
    /// </summary>
    internal static ImmutableDictionary<string, object?> Normalize(IDictionary<string, object?> properties) =>
        properties.ToImmutableDictionary(static p => p.Key, static p => NormalizeValue(p.Value));

    internal static object? NormalizeValue(object? value) => value switch
    {
        string or ImmutableDictionary<string, object?> or ImmutableArray<object?> => value,
        IDictionary<string, object?> map => map.ToImmutableDictionary(static p => p.Key, static p => NormalizeValue(p.Value)),
        IEnumerable<object?> list => list.Select(NormalizeValue).ToImmutableArray(),
        _ => value,
    };

    internal static CustomResourceOptions WithProviderVersion(CustomResourceOptions? options)
    {
        options = options is null ? new CustomResourceOptions() : CustomResourceOptions.Merge(options, new CustomResourceOptions());
        options.Version ??= ProviderVersion;
        return options;
    }

    private static string ResolveProviderVersion()
    {
        // The generated azure-native SDK carries its plugin version in AssemblyInformationalVersion
        // (e.g. "3.19.0+abc123"); untyped resources must pin the same version for plugin acquisition.
        var assembly = typeof(global::Pulumi.AzureNative.Resources.ResourceGroup).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (informational is not null)
        {
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus > 0 ? informational[..plus] : informational;
        }

        var version = assembly.GetName().Version ?? throw new InvalidOperationException(
            "Cannot determine the Pulumi.AzureNative package version for plugin acquisition.");
        return $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
