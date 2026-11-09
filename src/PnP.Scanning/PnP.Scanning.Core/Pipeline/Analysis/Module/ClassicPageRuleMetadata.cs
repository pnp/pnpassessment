#nullable enable
using System.Security.Cryptography;
using System.Text.Json;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;

namespace PnP.Scanning.Core.Pipeline.Analysis.Module;

internal static class ClassicPageRuleMetadata
{
    internal static string MappingDigest
    {
        get
        {
            using var resource = typeof(ClassicPageRuleMetadata).Assembly.GetManifestResourceStream(
                "PnP.Scanning.Core.Scanners.Classic.WebPartMapping.webpartmapping.xml")
                ?? throw new InvalidOperationException("The pinned mapping resource is unavailable.");
            return Convert.ToHexString(SHA256.HashData(resource)).ToLowerInvariant();
        }
    }

    internal static VersionedJson Pin(VersionedJson parameters)
    {
        Validate(parameters);
        return VersionedJson.From(new { mappingDigest = MappingDigest });
    }

    internal static void Validate(VersionedJson parameters)
    {
        if (parameters.SchemaVersion != 1 || parameters.Value.ValueKind != JsonValueKind.Object ||
            parameters.Value.EnumerateObject().Any(x => x.Name != "mappingDigest") ||
            parameters.Value.TryGetProperty("mappingDigest", out var digest) &&
            (digest.ValueKind != JsonValueKind.String || digest.GetString() != MappingDigest))
            throw new NotSupportedException("The selected Classic Page rule or pinned mapping resource is unavailable.");
    }
}
