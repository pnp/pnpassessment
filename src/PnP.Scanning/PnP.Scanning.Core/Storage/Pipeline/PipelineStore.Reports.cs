#nullable enable
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Pipeline.Collection.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;

namespace PnP.Scanning.Core.Storage.Pipeline;

/// <summary>
/// Exposes bound collection receipts and read-only results, and persists immutable report publication.
/// </summary>
internal sealed partial class PipelineStore
{
    /// <summary>
    /// Creates a receipt journal capability for the selected collection snapshot and phase.
    /// </summary>
    internal ICollectionJournal CollectionJournal(Guid assessmentId, Guid snapshotId, Guid runId) =>
        new CollectionJournalCapability(this, assessmentId, snapshotId, runId);
    /// <summary>
    /// Creates a read-only result capability for the selected analysis run.
    /// </summary>
    internal IAnalysisResultReader ResultReader(Guid assessmentId, Guid runId) => new ResultReadCapability(this, assessmentId, runId);
    /// <summary>
    /// Creates a report publication capability bound to the selected analysis run.
    /// </summary>
    internal IAnalysisReportWriter ReportWriter(Guid assessmentId, Guid runId) => new ReportWriteCapability(this, assessmentId, runId);

    /// <summary>
    /// Provides receipt commits and saved observation reads for one collection run.
    /// </summary>
    private sealed class CollectionJournalCapability(PipelineStore store, Guid assessmentId, Guid snapshotId, Guid runId) : ICollectionJournal
    {
        /// <summary>
        /// Commits the supplied receipt and checkpoint through the bound collection writer.
        /// </summary>
        public Task CommitAsync(CollectionRecord record, CancellationToken cancellationToken) =>
            store.CommitObservationAsync(assessmentId, snapshotId, runId, record, cancellationToken);
        /// <summary>
        /// Reads saved receipts in source identity order, optionally selecting metadata without the BLOB column.
        /// </summary>
        public async Task<IReadOnlyList<CollectedObservation>> ReadCommittedAsync(CancellationToken cancellationToken, bool metadataOnly = false)
        {
            using var db = store.CreateContext(assessmentId);
            var query = db.SourceObservations.AsNoTracking().Where(x => x.SnapshotId == snapshotId);
            if (metadataOnly) query = query.Select(x => new SourceObservationRow
            {
                ObservationId = x.ObservationId, SnapshotId = x.SnapshotId, SourceIdentity = x.SourceIdentity,
                SourceRevision = x.SourceRevision, AcquisitionStatus = x.AcquisitionStatus, MetadataJson = x.MetadataJson,
                AcquisitionError = x.AcquisitionError, Length = x.Length, Sha256 = x.Sha256,
            });
            var rows = await query.OrderBy(x => x.SourceIdentity).ToListAsync(cancellationToken);
            return rows.Select(x =>
            {
                if (!metadataOnly) SnapshotManifest.ValidateContent(x);
                return new CollectedObservation(x.ObservationId, x.SourceIdentity, x.SourceRevision, x.AcquisitionStatus,
                    new VersionedJson(x.MetadataJson), x.RawBytes?.ToArray(), x.AcquisitionError);
            }).ToArray();
        }
        /// <summary>
        /// Reads and validates one committed observation and returns detached fields and content bytes.
        /// </summary>
        public async Task<CollectedObservation> ReadAsync(Guid observationId, CancellationToken cancellationToken)
        {
            using var db = store.CreateContext(assessmentId);
            var row = await db.SourceObservations.AsNoTracking().SingleAsync(x => x.SnapshotId == snapshotId && x.ObservationId == observationId, cancellationToken);
            SnapshotManifest.ValidateContent(row);
            return new(row.ObservationId, row.SourceIdentity, row.SourceRevision, row.AcquisitionStatus,
                new VersionedJson(row.MetadataJson), row.RawBytes?.ToArray(), row.AcquisitionError);
        }
    }

    /// <summary>
    /// Restricts result reads to the committed payloads of one analysis run.
    /// </summary>
    private sealed class ResultReadCapability(PipelineStore store, Guid assessmentId, Guid runId) : IAnalysisResultReader
    {
        /// <summary>
        /// Returns detached committed results ordered by source observation ID.
        /// </summary>
        public async Task<IReadOnlyList<AnalysisResult>> ReadAsync(CancellationToken cancellationToken)
        {
            using var db = store.CreateContext(assessmentId);
            var rows = await db.AnalysisResults.AsNoTracking().Where(x => x.AnalysisRunId == runId)
                .OrderBy(x => x.ObservationId).ToListAsync(cancellationToken);
            return rows.Select(x => new AnalysisResult(x.ObservationId, x.Outcome, x.Reason, new VersionedJson(x.PayloadJson))).ToArray();
        }
    }

    /// <summary>
    /// Restricts report publication to a running analysis with complete source results and a valid sealed input.
    /// </summary>
    private sealed class ReportWriteCapability(PipelineStore store, Guid assessmentId, Guid runId) : IAnalysisReportWriter
    {
        /// <summary>
        /// Atomically publishes fixed report rows and their checkpoint digest, allowing only identical replay of an existing publication.
        /// </summary>
        public Task PublishAsync(IReadOnlyList<AnalysisReportRow> rows, CancellationToken cancellationToken) =>
            store.WriteAsync(assessmentId, async db =>
            {
                var run = await db.PhaseRuns.SingleAsync(x => x.RunId == runId, cancellationToken);
                if (run.Status != ScanStatus.Running || run.Kind != PhaseKind.Analysis)
                    throw new InvalidOperationException("Report writer requires a running analysis phase.");
                var snapshot = await db.SourceSnapshots.SingleAsync(x => x.SnapshotId == run.SnapshotId, cancellationToken);
                var manifest = await ValidateSnapshotAsync(db, snapshot, cancellationToken);
                var ids = await db.AnalysisResults.Where(x => x.AnalysisRunId == runId).Select(x => x.ObservationId).ToListAsync(cancellationToken);
                if (!ids.ToHashSet().SetEquals(manifest.Members.Select(x => x.ObservationId)))
                    throw new SnapshotIntegrityException("Reports require a result for every selected source record.");
                var existing = await db.ClassicPageReportRows.Where(x => x.AnalysisRunId == runId).ToListAsync(cancellationToken);
                if (existing.Count > 0)
                {
                    var previous = existing.ToDictionary(x => (x.Kind, x.RowKey));
                    if (previous.Count != rows.Count || rows.Any(x => !previous.TryGetValue((x.Kind, x.Key), out var old) ||
                        old.PayloadJson != x.Payload.Json || old.Ordinal != x.Ordinal))
                        throw new SnapshotIntegrityException("Report replay attempted to change published projections.");
                    return true;
                }
                db.ClassicPageReportRows.AddRange(rows.Select(x => new ClassicPageReportRow
                { AnalysisRunId = runId, Kind = x.Kind, RowKey = x.Key, Ordinal = x.Ordinal, PayloadJson = x.Payload.Json }));
                run.CheckpointJson = VersionedJson.From(new { stage = "ReportPublished", reportDigest = AnalysisReportDigest.Compute(rows) }).Json;
                return true;
            }, cancellationToken);
    }
}
