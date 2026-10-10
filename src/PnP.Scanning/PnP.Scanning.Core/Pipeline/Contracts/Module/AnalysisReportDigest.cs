using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PnP.Scanning.Core.Pipeline.Contracts.Module;

internal static class AnalysisReportDigest
{
    internal static string Compute(IEnumerable<AnalysisReportRow> rows) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(rows.OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => new { x.Kind, x.Key, x.Ordinal, payload = x.Payload.Json }))))).ToLowerInvariant();
}
