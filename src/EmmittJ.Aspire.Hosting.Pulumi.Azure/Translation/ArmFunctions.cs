// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;

namespace EmmittJ.Aspire.Hosting.Pulumi.Azure;

/// <summary>
/// Deterministic .NET implementations of the ARM template functions whose values must be computed
/// client-side during translation so that resource names stay identical to what an ARM deployment of the
/// same template would produce.
/// </summary>
/// <remarks>
/// Both implementations are derived from Microsoft's MIT-licensed ARM expression engine
/// (<c>Azure.Deployments.Expression</c> / <c>Microsoft.PowerPlatform.ResourceStack</c>):
/// <c>uniqueString</c> is a Murmur-variant 64-bit hash of the dash-joined arguments encoded as a
/// 13-character base32 string, and <c>guid</c> is a name-based (RFC 4122 version 5, SHA-1) UUID over the
/// dash-joined arguments in the ARM namespace. Stability matters: a wrong hash would rename every
/// <c>uniqueString</c>-suffixed resource relative to a native Aspire deployment of the same app.
/// </remarks>
internal static class ArmFunctions
{
    // The namespace GUID ARM uses for its guid() template function
    // (Azure.Deployments.Expression FunctionHelpers.ARMNamespaceGuid).
    private static readonly Guid ArmNamespaceGuid = new("11fb06fb-712d-4ddd-98c7-e71bbd588830");

    private const string Base32Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    /// <summary>Implements the ARM <c>uniqueString(...)</c> template function.</summary>
    /// <param name="values">The string arguments; they are joined with '-' before hashing.</param>
    /// <returns>The deterministic 13-character base32 hash.</returns>
    public static string UniqueString(params string[] values)
    {
        var input = Encoding.UTF8.GetBytes(string.Join('-', values));
        return Base32Encode(MurmurHash64(input));
    }

    /// <summary>Implements the ARM <c>guid(...)</c> template function.</summary>
    /// <param name="values">The string arguments; they are joined with '-' to form the UUIDv5 name.</param>
    /// <returns>The deterministic GUID in its canonical string form.</returns>
    public static string CreateGuid(params string[] values)
    {
        var name = Encoding.UTF8.GetBytes(string.Join('-', values));
        var namespaceBytes = ArmNamespaceGuid.ToByteArray();
        SwapByteOrder(namespaceBytes);

        byte[] hash;
        using (var sha1 = SHA1.Create())
        {
            sha1.TransformBlock(namespaceBytes, 0, namespaceBytes.Length, null, 0);
            sha1.TransformFinalBlock(name, 0, name.Length);
            hash = sha1.Hash!;
        }

        var guidBytes = new byte[16];
        Array.Copy(hash, 0, guidBytes, 0, 16);
        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | (5 << 4)); // version 5
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);     // RFC 4122 variant
        SwapByteOrder(guidBytes);
        return new Guid(guidBytes).ToString();
    }

    /// <summary>Implements the ARM <c>take(string, n)</c> semantics for strings.</summary>
    public static string Take(string value, int count) =>
        count <= 0 ? string.Empty : (value.Length <= count ? value : value[..count]);

    private static ulong MurmurHash64(byte[] data, uint seed = 0u)
    {
        var length = data.Length;
        var h1 = seed;
        var h2 = seed;
        int index;
        for (index = 0; index + 7 < length; index += 8)
        {
            var k1 = (uint)(data[index] | (data[index + 1] << 8) | (data[index + 2] << 16) | (data[index + 3] << 24));
            var k2 = (uint)(data[index + 4] | (data[index + 5] << 8) | (data[index + 6] << 16) | (data[index + 7] << 24));

            k1 *= 597399067u;
            k1 = uint.RotateLeft(k1, 15);
            k1 *= 2869860233u;
            h1 ^= k1;
            h1 = uint.RotateLeft(h1, 19);
            h1 += h2;
            h1 = h1 * 5 + 1444728091u;

            k2 *= 2869860233u;
            k2 = uint.RotateLeft(k2, 17);
            k2 *= 597399067u;
            h2 ^= k2;
            h2 = uint.RotateLeft(h2, 13);
            h2 += h1;
            h2 = h2 * 5 + 197830471u;
        }

        var tail = length - index;
        if (tail > 0)
        {
            var k1 = tail switch
            {
                >= 4 => (uint)(data[index] | (data[index + 1] << 8) | (data[index + 2] << 16) | (data[index + 3] << 24)),
                3 => (uint)(data[index] | (data[index + 1] << 8) | (data[index + 2] << 16)),
                2 => (uint)(data[index] | (data[index + 1] << 8)),
                _ => data[index],
            };

            k1 *= 597399067u;
            k1 = uint.RotateLeft(k1, 15);
            k1 *= 2869860233u;
            h1 ^= k1;

            if (tail > 4)
            {
                var k2 = (uint)(tail switch
                {
                    7 => data[index + 4] | (data[index + 5] << 8) | (data[index + 6] << 16),
                    6 => data[index + 4] | (data[index + 5] << 8),
                    _ => data[index + 4],
                } * -1425107063);
                k2 = uint.RotateLeft(k2, 17);
                k2 *= 597399067u;
                h2 ^= k2;
            }
        }

        h1 ^= (uint)length;
        h2 ^= (uint)length;
        h1 += h2;
        h2 += h1;

        h1 ^= h1 >> 16;
        h1 *= 2246822507u;
        h1 ^= h1 >> 13;
        h1 *= 3266489909u;
        h1 ^= h1 >> 16;

        h2 ^= h2 >> 16;
        h2 *= 2246822507u;
        h2 ^= h2 >> 13;
        h2 *= 3266489909u;
        h2 ^= h2 >> 16;

        h1 += h2;
        h2 += h1;
        return ((ulong)h2 << 32) | h1;
    }

    private static string Base32Encode(ulong input)
    {
        var sb = new StringBuilder(13);
        for (var i = 0; i < 13; i++)
        {
            sb.Append(Base32Alphabet[(int)(input >> 59)]);
            input <<= 5;
        }

        return sb.ToString();
    }

    private static void SwapByteOrder(byte[] guid)
    {
        (guid[0], guid[3]) = (guid[3], guid[0]);
        (guid[1], guid[2]) = (guid[2], guid[1]);
        (guid[4], guid[5]) = (guid[5], guid[4]);
        (guid[6], guid[7]) = (guid[7], guid[6]);
    }
}
