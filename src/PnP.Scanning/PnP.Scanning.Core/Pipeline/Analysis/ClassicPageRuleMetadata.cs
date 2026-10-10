#nullable enable
using System.Security.Cryptography;
using System.Text.Json;
using PnP.Scanning.Core.Pipeline.Contracts;

namespace PnP.Scanning.Core.Pipeline.Analysis;

internal static class ClassicPageRuleMetadata
{
    internal static string MappingDigest
    {
        get
        {
            using var resource = typeof(ClassicPageRuleMetadata).Assembly.GetManifestResourceStream("PnP.Scanning.Core.Pipeline.Analysis.WebPartMapping.webpartmapping.xml")
                ?? throw new InvalidOperationException("The pinned mapping resource is unavailable.");
            return Convert.ToHexString(SHA256.HashData(resource)).ToLowerInvariant();
        }
    }
    internal static VersionedJson Pin(VersionedJson parameters)
    {
        Validate(parameters);
        return parameters.Value.EnumerateObject().Any() ? parameters : VersionedJson.From(new { mappingDigest = MappingDigest });
    }
    internal static void Validate(VersionedJson parameters)
    {
        if (parameters.SchemaVersion != 1 || parameters.Value.ValueKind != JsonValueKind.Object ||
            parameters.Value.EnumerateObject().Any(x => x.Name != "mappingDigest") ||
            parameters.Value.TryGetProperty("mappingDigest", out var digest) && digest.GetString() != MappingDigest)
            throw new NotSupportedException("Classic Page analysis requires its registered mapping resource; the pinned mapping version is unavailable.");
    }
}
