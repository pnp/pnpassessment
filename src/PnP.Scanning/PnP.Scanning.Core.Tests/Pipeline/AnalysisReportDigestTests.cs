#nullable enable
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using Xunit;

namespace PnP.Scanning.Core.Tests.Pipeline;

/// <summary>Proves streaming digests retain the published JSON byte contract and support actual tenant-scale payloads.</summary>
public sealed class AnalysisReportDigestTests
{
    /// <summary>Checks every UTF-16 code unit, escaping, buffer boundaries, stable sorting and empty publication against the original algorithm.</summary>
    [Fact]
    public void Streaming_matches_existing_serialized_bytes()
    {
        string characters = new(Enumerable.Range(0, char.MaxValue + 1).Select(value => (char)value).ToArray());
        AnalysisReportRow[] rows =
        [
            new("z", "later", 17, VersionedJson.From(new { text = characters })),
            new("a<&", "\"key\\\n", 0, VersionedJson.From(new { text = new string('a', 8191) + "\ud83d\ude00" })),
            new("a<&", "second", 2, VersionedJson.From(new { text = "\ud800\udfff\ud800\u0000\u2028\u2029" })),
            new(characters, "raw", 1, new VersionedJson("{\"schemaVersion\":1,\"value\":\"\u00e9\ud83d\ude00\"}")),
        ];
        foreach (AnalysisReportRow[] publication in new[]
                 {
                     rows, rows.Reverse().ToArray(), Array.Empty<AnalysisReportRow>()
                 })
        {
            string original = JsonSerializer.Serialize(
                publication.OrderBy(row => row.Kind, StringComparer.Ordinal)
                    .ThenBy(row => row.Key, StringComparer.Ordinal)
                    .Select(row => new { row.Kind, row.Key, row.Ordinal, payload = row.Payload.Json }));
            string expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(original))).ToLowerInvariant();
            Assert.Equal(expected, AnalysisReportDigest.Compute(publication));
        }
    }

    /// <summary>Exercises the observed oversized-string threshold and independently hashes every payload byte without allocating its escaped representation.</summary>
    [Fact]
    public void Actual_large_payload_bypasses_single_JSON_token_limit_without_truncation()
    {
        const int payloadLength = 227_336_104;
        const string prefix = "{\"schemaVersion\":1,\"value\":\"";
        const string suffix = "\"}";
        int textLength = payloadLength - prefix.Length - suffix.Length;
        VersionedJson payload = new(prefix + new string('x', textLength) + suffix);
        Assert.Equal(payloadLength, payload.Json.Length);
        AnalysisReportRow[] rows = [new("fixture", "large", 0, payload)];
        Assert.Throws<ArgumentException>(() => JsonSerializer.Serialize(
            rows.Select(row => new { row.Kind, row.Key, row.Ordinal, payload = row.Payload.Json })));

        using IncrementalHash expected = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        expected.AppendData(
            Encoding.UTF8.GetBytes(
                "[{\"Kind\":\"fixture\",\"Key\":\"large\",\"Ordinal\":0,\"payload\":\"" +
                "{\\u0022schemaVersion\\u0022:1,\\u0022value\\u0022:\\u0022"));
        byte[] block = Encoding.UTF8.GetBytes(new string('x', 8192));
        for (int remaining = textLength; remaining > 0; remaining -= block.Length)
            expected.AppendData(block.AsSpan(0, Math.Min(remaining, block.Length)));
        expected.AppendData(Encoding.UTF8.GetBytes("\\u0022}\"}]"));
        Assert.Equal(
            Convert.ToHexString(expected.GetHashAndReset()).ToLowerInvariant(),
            AnalysisReportDigest.Compute(rows));
        Assert.Equal(payloadLength, payload.Json.Length);
    }

    /// <summary>A cancelled publication cannot acquire a digest or rows, and its unchanged payload can subsequently publish normally.</summary>
    [Fact]
    public async Task Cancelled_report_publication_does_not_commit()
    {
        using PipelineStoreFixture fixture = new();
        await fixture.InitializeAsync();
        await fixture.Writer.SealAsync(CancellationToken.None);
        Guid run = await fixture.StartAnalysisAsync();
        IAnalysisReportWriter writer = fixture.Store.ReportWriter(fixture.AssessmentId, run);
        AnalysisReportRow[] rows = [new("fixture", "cancelled", 0, VersionedJson.Empty)];
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.PublishAsync(rows, cancellation.Token));
        using (PnP.Scanning.Core.Storage.ScanContext context = fixture.Store.CreateContext(
                   fixture.AssessmentId,
                   readOnly: true))
        {
            Assert.Empty(await context.AnalysisReportRows.ToListAsync());
            Assert.Null((await context.AnalysisRuns.SingleAsync()).ReportDigest);
        }

        await writer.PublishAsync(rows, CancellationToken.None);
        Assert.Equal(
            rows,
            await fixture.Store.ReportReader(fixture.AssessmentId, run).ReadAsync(CancellationToken.None));
    }
}