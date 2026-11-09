#nullable enable
using System.Security.Cryptography;
using System.Text.Json;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;

namespace PnP.Scanning.Core.Pipeline.Analysis.Module;

/// <summary>
/// Pins and validates the mapping resource used by the selected analysis rule.
/// </summary>
internal static class ClassicPageRuleMetadata
{
    internal static string MappingDigest
    {
        get
        {
            using Stream? resource =
                typeof(ClassicPageRuleMetadata).Assembly.GetManifestResourceStream(
                    "PnP.Scanning.Core.Scanners.Classic.WebPartMapping.webpartmapping.xml") ??
                throw new InvalidOperationException("The pinned mapping resource is unavailable.");
            return Convert.ToHexString(SHA256.HashData(resource)).ToLowerInvariant();
        }
    }

    /// <summary>
    /// Records the available mapping digest after validating supplied rule parameters.
    /// </summary>
    internal static VersionedJson Pin(VersionedJson parameters)
    {
        Validate(parameters);
        return VersionedJson.From(new { mappingDigest = MappingDigest });
    }

    /// <summary>
    /// Rejects unsupported schemas, parameter names, or unavailable pinned mapping digests.
    /// </summary>
    internal static void Validate(VersionedJson parameters)
    {
        if (parameters.SchemaVersion != 1 || parameters.Value.ValueKind != JsonValueKind.Object ||
            parameters.Value.EnumerateObject().Any(x => x.Name != "mappingDigest") ||
            parameters.Value.TryGetProperty("mappingDigest", out JsonElement digest) &&
            (digest.ValueKind != JsonValueKind.String || digest.GetString() != MappingDigest))
            throw new NotSupportedException(
                "The selected Classic Page rule or pinned mapping resource is unavailable.");
    }
}