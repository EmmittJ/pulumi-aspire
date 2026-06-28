// Licensed under the Apache License, Version 2.0.

using Aspire.Hosting.ApplicationModel;

namespace EmmittJ.Aspire.Hosting.Pulumi;

/// <summary>
/// Compares resources by name. Used to key translated-resource and deployment-target lookups so that
/// references created through different resource instances (for example via a polyglot AppHost bridge)
/// still resolve to the same entry.
/// </summary>
internal sealed class ResourceNameComparer : IEqualityComparer<IResource>
{
    public static ResourceNameComparer Instance { get; } = new();

    public bool Equals(IResource? x, IResource? y)
    {
        if (ReferenceEquals(x, y))
        {
            return true;
        }

        if (x is null || y is null)
        {
            return false;
        }

        return string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
    }

    public int GetHashCode(IResource obj) => StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name);
}
