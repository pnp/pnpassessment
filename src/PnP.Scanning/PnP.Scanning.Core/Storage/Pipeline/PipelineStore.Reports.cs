#nullable enable
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;

namespace PnP.Scanning.Core.Storage.Pipeline;

internal sealed partial class PipelineStore
{
    internal IAnalysisResultReader ResultReader(Guid assessmentId, Guid runId) =>
        new ResultReadCapability(this, assessmentId, runId);

    internal IAnalysisReportWriter ReportWriter(Guid assessmentId, Guid runId) =>
        new ReportWriteCapability(this, assessmentId, runId);

    internal IAnalysisReportReader ReportReader(Guid assessmentId, Guid runId) =>
        new ReportReadCapability(this, assessmentId, runId);

    private sealed class ResultReadCapability(PipelineStore store, Guid assessmentId, Guid runId) : IAnalysisResultReader
    {
        public async Task<IReadOnlyList<AnalysisResult>> ReadAsync(CancellationToken cancellationToken)
        {
            using var db = store.CreateContext(assessmentId, readOnly: true);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            _ = await AnalysisManifestAsync(db, runId, cancellationToken);
            _ = await CompletedResultsAsync(db, runId, cancellationToken);
            var rows = await db.AnalysisResults.AsNoTracking().Where(x => x.AnalysisRunId == runId)
                .OrderBy(x => x.ObservationId).ToListAsync(cancellationToken);
            return rows.Select(x => new AnalysisResult(x.ObservationId, x.Outcome, x.Reason, new VersionedJson(x.PayloadJson))).ToArray();
        }
    }

    private sealed class ReportReadCapability(PipelineStore store, Guid assessmentId, Guid runId) : IAnalysisReportReader
    {
        public async Task<IReadOnlyList<AnalysisReportRow>> ReadAsync(CancellationToken cancellationToken)
        {
            using var db = store.CreateContext(assessmentId, readOnly: true);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            _ = await AnalysisManifestAsync(db, runId, cancellationToken);
            return await ReadReportsAsync(db, runId, cancellationToken);
        }
    }

    private static async Task<IReadOnlyList<AnalysisReportRow>> ReadReportsAsync(ScanContext db, Guid runId, CancellationToken token)
    {
        var run = await db.AnalysisRuns.AsNoTracking().SingleAsync(x => x.AnalysisRunId == runId, token);
        if (run.ReportDigest == null || run.ReportPublishedAtUtc == null)
            throw new SnapshotIntegrityException("The analysis has not published report rows.");
        var rows = (await db.AnalysisReportRows.AsNoTracking().Where(x => x.AnalysisRunId == runId).ToListAsync(token))
            .Select(x => new AnalysisReportRow(x.Kind, x.RowKey, x.Ordinal, new VersionedJson(x.PayloadJson)))
            .OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Ordinal).ThenBy(x => x.Key, StringComparer.Ordinal).ToArray();
        if (AnalysisReportDigest.Compute(rows) != run.ReportDigest)
            throw new SnapshotIntegrityException("Published report rows differ from their committed digest.");
        return rows;
    }

    private sealed class ReportWriteCapability(PipelineStore store, Guid assessmentId, Guid runId) : IAnalysisReportWriter
    {
        public Task PublishAsync(IReadOnlyList<AnalysisReportRow> rows, CancellationToken cancellationToken)
        {
            var publication = rows.ToArray();
            if (publication.Any(x => string.IsNullOrWhiteSpace(x.Kind) || string.IsNullOrWhiteSpace(x.Key) ||
                    x.Ordinal < 0 || x.Payload == null) ||
                publication.Select(x => (x.Kind, x.Key)).Distinct().Count() != publication.Length)
                throw new ArgumentException("Report rows require unique kind/key pairs, nonnegative ordinals and versioned payloads.", nameof(rows));
            var digest = AnalysisReportDigest.Compute(publication);
            return store.WriteAsync(assessmentId, async db =>
            {
                var phase = await db.PhaseRuns.SingleAsync(x => x.RunId == runId, cancellationToken);
                if (phase.Status != ScanStatus.Running || phase.Kind != PhaseKind.Analysis)
                    throw new InvalidOperationException("Reports require a running analysis phase.");
                var manifest = await AnalysisManifestAsync(db, runId, cancellationToken);
                var completed = await CompletedResultsAsync(db, runId, cancellationToken);
                if (!completed.SetEquals(manifest.Members.Keys))
                    throw new SnapshotIntegrityException("Reports require a result for every selected source record.");
                var run = await db.AnalysisRuns.SingleAsync(x => x.AnalysisRunId == runId, cancellationToken);
                if (run.ReportDigest != null)
                {
                    _ = await ReadReportsAsync(db, runId, cancellationToken);
                    if (run.ReportDigest != digest)
                        throw new SnapshotIntegrityException("Report replay attempted to change published rows.");
                    return false;
                }
                if (await db.AnalysisReportRows.AnyAsync(x => x.AnalysisRunId == runId, cancellationToken))
                    throw new SnapshotIntegrityException("Unpublished report rows already exist.");
                db.AnalysisReportRows.AddRange(publication.Select(x => new AnalysisReportRowEntry
                {
                    AnalysisRunId = runId, Kind = x.Kind, RowKey = x.Key, Ordinal = x.Ordinal, PayloadJson = x.Payload.Json,
                }));
                run.ReportDigest = digest;
                run.ReportPublishedAtUtc = DateTime.UtcNow;
                phase.CheckpointJson = VersionedJson.From(new { stage = "ReportPublished", reportDigest = digest }).Json;
                return true;
            }, cancellationToken);
        }
    }
}
