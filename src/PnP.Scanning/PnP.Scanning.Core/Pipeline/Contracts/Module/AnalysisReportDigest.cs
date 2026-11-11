using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Globalization;

namespace PnP.Scanning.Core.Pipeline.Contracts.Module;

/// <summary>Hashes the established report-row JSON representation without buffering or size-limiting escaped payload strings.</summary>
internal static class AnalysisReportDigest
{
    /// <summary>Streams the same ordered properties, default JSON escaping and BOM-free UTF-8 bytes used by existing published digests.</summary>
    internal static string Compute(IEnumerable<AnalysisReportRow> rows)
    {
        using SHA256 algorithm = SHA256.Create();
        using CryptoStream crypto = new(Stream.Null, algorithm, CryptoStreamMode.Write);
        using (StreamWriter output = new(crypto, new UTF8Encoding(false), 8192, leaveOpen: true))
        {
            output.Write('[');
            bool first = true;
            foreach (AnalysisReportRow row in rows.OrderBy(x => x.Kind, StringComparer.Ordinal)
                         .ThenBy(x => x.Key, StringComparer.Ordinal))
            {
                if (!first) output.Write(',');
                first = false;
                output.Write("{\"Kind\":");
                WriteString(output, row.Kind);
                output.Write(",\"Key\":");
                WriteString(output, row.Key);
                output.Write(",\"Ordinal\":");
                output.Write(row.Ordinal.ToString(CultureInfo.InvariantCulture));
                output.Write(",\"payload\":");
                WriteString(output, row.Payload.Json);
                output.Write('}');
            }

            output.Write(']');
        }

        crypto.FlushFinalBlock();
        return Convert
            .ToHexString(algorithm.Hash ?? throw new CryptographicException("The report digest did not finalize."))
            .ToLowerInvariant();
    }

    /// <summary>Uses the default JSON encoder's bounded writer path, retaining escaping and surrogate handling without a single-token allocation.</summary>
    private static void WriteString(TextWriter output, string value)
    {
        output.Write('"');
        JavaScriptEncoder.Default.Encode(output, value);
        output.Write('"');
    }
}