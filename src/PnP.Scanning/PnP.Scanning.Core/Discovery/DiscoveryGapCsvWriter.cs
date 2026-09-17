using System.Text;

namespace PnP.Scanning.Core.Discovery;

internal static class DiscoveryGapCsvWriter
{
    internal static async Task WriteAsync(string path, IReadOnlyList<DiscoveryGapDetailRow> gaps,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                         FileShare.None, 64 * 1024, useAsync: true))
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            await writer.WriteLineAsync("ScopeKey,SourceKind,GapCode,Detail,Resolved".AsMemory(), cancellationToken);
            foreach (var gap in gaps.OrderBy(row => row.ScopeKey, StringComparer.Ordinal)
                         .ThenBy(row => row.Code, StringComparer.Ordinal).ThenBy(row => row.GapKey, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = string.Join(',', Csv(gap.ScopeKey), Csv(gap.SourceKind), Csv(gap.Code),
                    Csv(gap.Detail), gap.Resolved ? "true" : "false");
                await writer.WriteLineAsync(row.AsMemory(), cancellationToken);
            }
            await writer.FlushAsync(cancellationToken);
        }
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static string Csv(string value)
    {
        value ??= string.Empty;
        return value.IndexOfAny([',', '"', '\r', '\n']) < 0
            ? value
            : '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
    }
}
