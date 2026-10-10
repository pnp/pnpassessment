#nullable enable
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Pipeline.Collection;
using PnP.Scanning.Core.Pipeline.Contracts;

namespace PnP.Scanning.Core.Storage.Pipeline;

internal sealed partial class PipelineStore
{
    internal ICollectionJournal CollectionJournal(Guid assessmentId, Guid snapshotId, Guid runId) =>
        new CollectionJournalCapability(this, assessmentId, snapshotId, runId);
    internal IAnalysisResultReader ResultReader(Guid assessmentId, Guid runId) => new ResultReadCapability(this, assessmentId, runId);
    internal IAnalysisReportWriter ReportWriter(Guid assessmentId, Guid runId) => new ReportWriteCapability(this, assessmentId, runId);

    private sealed class CollectionJournalCapability(PipelineStore store, Guid assessmentId, Guid snapshotId, Guid runId) : ICollectionJournal
    {
        public Task CommitAsync(CollectionRecord record, CancellationToken cancellationToken) =>
            store.CommitObservationAsync(assessmentId, snapshotId, runId, record, cancellationToken);
        public async Task<IReadOnlyList<CollectedObservation>> ReadCommittedAsync(CancellationToken cancellationToken, bool metadataOnly = false)
        {
            using var db = store.CreateContext(assessmentId);
            var rows = await db.SourceObservations.AsNoTracking().Where(x => x.SnapshotId == snapshotId)
                .OrderBy(x => x.SourceIdentity).ToListAsync(cancellationToken);
            var query = db.SourceArtifacts.AsNoTracking().Where(x => x.SnapshotId == snapshotId);
            var artifacts = await (metadataOnly ? query.Select(x => new SourceArtifactRow { ArtifactId = x.ArtifactId, SnapshotId = x.SnapshotId,
                ObservationId = x.ObservationId, Length = x.Length, Sha256 = x.Sha256 }) : query).ToDictionaryAsync(x => x.ArtifactId, cancellationToken);
            return rows.Select(x =>
            {
                if (!artifacts.TryGetValue(x.ArtifactId, out var artifact) || artifact.ObservationId != x.ObservationId)
                    throw new SnapshotIntegrityException("Collection receipt has an invalid artifact reference.");
                if (!metadataOnly) SnapshotManifest.ValidateArtifact(artifact, x.AcquisitionStatus);
                return new CollectedObservation(x.ObservationId, x.SourceIdentity, x.SourceRevision, x.AcquisitionStatus,
                    new VersionedJson(x.MetadataJson), artifact.RawBytes?.ToArray(), x.AcquisitionError);
            }).ToArray();
        }
        public async Task<CollectedObservation> ReadAsync(Guid observationId, CancellationToken cancellationToken)
        {
            using var db = store.CreateContext(assessmentId);
            var row = await db.SourceObservations.AsNoTracking().SingleAsync(x => x.SnapshotId == snapshotId && x.ObservationId == observationId, cancellationToken);
            var artifact = await db.SourceArtifacts.AsNoTracking().SingleAsync(x => x.ArtifactId == row.ArtifactId && x.ObservationId == observationId && x.SnapshotId == snapshotId, cancellationToken);
            SnapshotManifest.ValidateArtifact(artifact, row.AcquisitionStatus);
            return new(row.ObservationId, row.SourceIdentity, row.SourceRevision, row.AcquisitionStatus,
                new VersionedJson(row.MetadataJson), artifact.RawBytes?.ToArray(), row.AcquisitionError);
        }
    }

    private sealed class ResultReadCapability(PipelineStore store, Guid assessmentId, Guid runId) : IAnalysisResultReader
    {
        public async Task<IReadOnlyList<AnalysisResult>> ReadAsync(CancellationToken cancellationToken)
        {
            using var db = store.CreateContext(assessmentId);
            var rows = await db.AnalysisResults.AsNoTracking().Where(x => x.AnalysisRunId == runId)
                .OrderBy(x => x.ObservationId).ToListAsync(cancellationToken);
            return rows.Select(x => new AnalysisResult(x.ObservationId, x.Outcome, x.Reason, new VersionedJson(x.PayloadJson))).ToArray();
        }
    }

    private sealed class ReportWriteCapability(PipelineStore store, Guid assessmentId, Guid runId) : IAnalysisReportWriter
    {
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
