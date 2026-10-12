using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PnP.Scanning.Core.Pipeline.Contracts.Module;

/// <summary>
/// Computes the deterministic integrity digest shared by report publication and export.
/// </summary>
internal static class AnalysisReportDigest
{
    /// <summary>
    /// Orders report kinds and stable row keys, then hashes their keys, ordinals and serialized payloads with SHA-256.
    /// </summary>
    internal static string Compute(IEnumerable<AnalysisReportRow> rows) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(rows.OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => new { x.Kind, x.Key, x.Ordinal, payload = x.Payload.Json }))))).ToLowerInvariant();
}
