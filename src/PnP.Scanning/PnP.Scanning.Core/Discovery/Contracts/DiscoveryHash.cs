using System.Security.Cryptography;
using System.Text;

namespace PnP.Scanning.Core.Discovery;

internal static class DiscoveryHash
{
    internal static string Of(params string[] values)
    {
        using var sha = SHA256.Create();
        var canonical = string.Join("|", values.Select(value =>
        {
            value ??= string.Empty;
            return value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + value;
        }));
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
