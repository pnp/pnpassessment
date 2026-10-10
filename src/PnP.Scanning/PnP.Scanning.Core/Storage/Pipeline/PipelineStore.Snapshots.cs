#nullable enable
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Pipeline.Contracts;

namespace PnP.Scanning.Core.Storage.Pipeline;

internal sealed partial class PipelineStore
{
    private Task<bool> CommitObservationAsync(Guid assessmentId, Guid snapshotId, Guid collectionRunId,
        CollectionRecord record, CancellationToken token) => WriteAsync(assessmentId, async db =>
    {
        var snapshot = await db.SourceSnapshots.SingleAsync(x => x.SnapshotId == snapshotId, token);
        if (snapshot.IsSealed) throw new InvalidOperationException("A sealed snapshot cannot be changed.");
        var run = await db.PhaseRuns.SingleAsync(x => x.RunId == collectionRunId, token);
        if (run.Kind != PhaseKind.Collection || run.SnapshotId != snapshotId || run.Status != ScanStatus.Running)
            throw new InvalidOperationException("Collection writer is not bound to a running collection phase.");
        var item = record.Observation;
        if (item == null)
        {
            run.CheckpointJson = record.Checkpoint.Json;
            return true;
        }
        if (item.ObservationId == Guid.Empty || string.IsNullOrWhiteSpace(item.SourceIdentity) || !Enum.IsDefined(item.AcquisitionStatus))
            throw new ArgumentException("A source observation requires an ID, identity and acquisition status.");
        if (item.AcquisitionStatus == AcquisitionStatus.Complete && item.RawBytes == null)
            throw new ArgumentException("Complete acquisition requires a BLOB; an empty BLOB is valid.");

        var previous = await db.SourceObservations.SingleOrDefaultAsync(x => x.ObservationId == item.ObservationId, token);
        if (previous != null)
        {
            var previousArtifact = await db.SourceArtifacts.SingleAsync(x => x.ArtifactId == previous.ArtifactId, token);
            if (previous.SnapshotId != snapshotId || previous.SourceIdentity != item.SourceIdentity ||
                previous.SourceRevision != item.SourceRevision || previous.AcquisitionStatus != item.AcquisitionStatus ||
                previous.MetadataJson != item.Metadata.Json || previous.AcquisitionError != item.AcquisitionError ||
                !SameBytes(previousArtifact.RawBytes, item.RawBytes))
                throw new SnapshotIntegrityException("Replay attempted to replace an existing source observation.");
            SnapshotManifest.ValidateArtifact(previousArtifact, previous.AcquisitionStatus);
        }
        else
        {
            var artifactId = Guid.NewGuid();
            var bytes = item.RawBytes?.ToArray();
            db.SourceObservations.Add(new()
            {
                ObservationId = item.ObservationId, SnapshotId = snapshotId, SourceIdentity = item.SourceIdentity,
                SourceRevision = item.SourceRevision, AcquisitionStatus = item.AcquisitionStatus,
                MetadataJson = item.Metadata.Json, AcquisitionError = item.AcquisitionError, ArtifactId = artifactId,
            });
            db.SourceArtifacts.Add(new()
            {
                ArtifactId = artifactId, ObservationId = item.ObservationId, SnapshotId = snapshotId,
                RawBytes = bytes, Length = bytes?.LongLength, Sha256 = bytes == null ? null : SnapshotManifest.Digest(bytes),
            });
            run.CompletedRecords++;
            string? error = item.AcquisitionStatus == AcquisitionStatus.Complete ? null :
                $"{item.AcquisitionStatus}: {item.AcquisitionError ?? "content acquisition was not complete"}";
            if (error != null) AddError(run, error, item.ObservationId);
            if (record.ExpectedRecords is int expected)
            {
                if (expected < run.CompletedRecords) throw new ArgumentException("Expected records are less than committed records.");
                run.TotalRecords = expected;
            }
            await UpdateParentAsync(db, run, error, item.ObservationId);
        }
        // Source, artifact, progress and the opaque collector checkpoint are one transaction.
        run.CheckpointJson = record.Checkpoint.Json;
        return true;
    }, token);

    private Task<SnapshotInfo> SealAsync(Guid assessmentId, Guid snapshotId, Guid collectionRunId, CancellationToken token) =>
        WriteAsync(assessmentId, async db =>
        {
            var snapshot = await db.SourceSnapshots.SingleAsync(x => x.SnapshotId == snapshotId, token);
            var run = await db.PhaseRuns.SingleAsync(x => x.RunId == collectionRunId, token);
            if (run.Kind != PhaseKind.Collection || run.SnapshotId != snapshotId || run.Status != ScanStatus.Running)
                throw new InvalidOperationException("Only a running collection phase can seal its snapshot.");
            if (snapshot.IsSealed) return (await ValidateSnapshotAsync(db, snapshot, token)).Info();
            var observations = await db.SourceObservations.AsNoTracking().Where(x => x.SnapshotId == snapshotId).ToListAsync(token);
            var artifacts = await ReadAndValidateArtifactsAsync(db, snapshotId, observations, token);
            var manifest = SnapshotManifest.Build(snapshot, observations, artifacts, artifactsVerified: true);
            if (run.CompletedRecords != observations.Count || (run.TotalRecords > 0 && run.TotalRecords != observations.Count))
                throw new SnapshotIntegrityException("Collection progress does not match committed snapshot membership.");
            snapshot.ManifestJson = manifest.Serialize();
            snapshot.MemberIdsJson = manifest.MemberIds();
            snapshot.ManifestDigest = SnapshotManifest.Digest(snapshot.ManifestJson);
            snapshot.SealedAtUtc = DateTime.UtcNow;
            snapshot.IsSealed = true;
            run.TotalRecords = observations.Count;
            run.Status = ScanStatus.Finished;
            run.EndedAtUtc = DateTime.UtcNow;
            run.CheckpointJson = VersionedJson.From(new { stage = "Sealed", snapshotId, manifestDigest = snapshot.ManifestDigest }).Json;
            if (run.ParentRunId is Guid parentId)
            {
                var parent = await db.PhaseRuns.SingleAsync(x => x.RunId == parentId, token);
                parent.CurrentPhase = PhaseKind.Analysis;
                parent.CompletedRecords = 0;
                parent.TotalRecords = observations.Count;
                parent.CheckpointJson = run.CheckpointJson;
            }
            else await UpdateAssessmentAsync(db, assessmentId, ScanStatus.Finished);
            return manifest.Info();
        }, token);

    internal async Task<SnapshotInfo> OpenSnapshotAsync(Guid assessmentId, Guid snapshotId, CancellationToken token = default)
        => (await OpenManifestAsync(assessmentId, snapshotId, token)).Info();

    private async Task<SnapshotManifest> OpenManifestAsync(Guid assessmentId, Guid snapshotId, CancellationToken token)
    {
        await EnsureDatabaseAsync(assessmentId, token);
        using var db = CreateContext(assessmentId);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var snapshot = await db.SourceSnapshots.AsNoTracking().SingleOrDefaultAsync(x => x.SnapshotId == snapshotId, token)
            ?? throw new NotSupportedException("The selected assessment has no such source snapshot; legacy rows cannot supply raw evidence.");
        return await ValidateSnapshotAsync(db, snapshot, token);
    }

    private static async Task<SnapshotManifest> ValidateSnapshotAsync(ScanContext db, SourceSnapshotRow snapshot, CancellationToken token)
    {
        if (!snapshot.IsSealed || snapshot.SealedAtUtc == null || snapshot.ManifestJson == null || snapshot.MemberIdsJson == null ||
            snapshot.ManifestDigest == null)
            throw new SnapshotIntegrityException("Analysis requires an explicitly selected, sealed snapshot.");
        if (SnapshotManifest.Digest(snapshot.ManifestJson) != snapshot.ManifestDigest)
            throw new SnapshotIntegrityException("Snapshot manifest SHA-256 mismatch.");
        var observations = await db.SourceObservations.AsNoTracking().Where(x => x.SnapshotId == snapshot.SnapshotId).ToListAsync(token);
        var artifacts = await ReadAndValidateArtifactsAsync(db, snapshot.SnapshotId, observations, token);
        var actual = SnapshotManifest.Build(snapshot, observations, artifacts, artifactsVerified: true);
        if (actual.Serialize() != snapshot.ManifestJson || actual.MemberIds() != snapshot.MemberIdsJson)
            throw new SnapshotIntegrityException("Snapshot membership, identity, revision or artifact reference differs from its sealed manifest.");
        return actual;
    }

    private async Task<SourceRecord> ReadObservationAsync(Guid assessmentId, Guid snapshotId, Guid observationId, PinnedManifest pinned, CancellationToken token)
    {
        await EnsureDatabaseAsync(assessmentId, token);
        using var db = CreateContext(assessmentId);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var snapshot = await db.SourceSnapshots.AsNoTracking().SingleAsync(x => x.SnapshotId == snapshotId, token);
        return await ReadSelectedObservationAsync(db, snapshot, observationId, pinned, token);
    }

    private static async Task<SourceRecord> ReadSelectedObservationAsync(ScanContext db, SourceSnapshotRow snapshot,
        Guid observationId, PinnedManifest pinned, CancellationToken token)
    {
        if (!snapshot.IsSealed || snapshot.ManifestJson != pinned.Json || snapshot.ManifestDigest != pinned.Digest ||
            snapshot.MemberIdsJson != pinned.MemberIdsJson)
            throw new SnapshotIntegrityException("Snapshot is no longer sealed or its manifest is corrupt.");
        var manifest = pinned.Manifest;
        if (!pinned.Members.TryGetValue(observationId, out var expected))
            throw new SnapshotIntegrityException("Requested observation is not a member of the selected snapshot.");
        var observation = await db.SourceObservations.AsNoTracking().SingleOrDefaultAsync(x => x.ObservationId == observationId, token)
            ?? throw new SnapshotIntegrityException($"Missing source observation {observationId}.");
        var artifact = await db.SourceArtifacts.AsNoTracking().SingleOrDefaultAsync(x => x.ArtifactId == observation.ArtifactId, token)
            ?? throw new SnapshotIntegrityException($"Missing source artifact {observation.ArtifactId}.");
        // Validate the whole envelope and this record against the previously sealed member mapping.
        var actual = SnapshotManifest.Build(snapshot, [observation], [artifact]);
        if (manifest.FormatVersion != snapshot.FormatVersion || manifest.AssessmentId != snapshot.AssessmentId || manifest.SnapshotId != snapshot.SnapshotId ||
            manifest.ModuleKey != snapshot.ModuleKey || manifest.InputVersion != snapshot.InputVersion || manifest.ScopeJson != snapshot.ScopeJson ||
            actual.Members.Single() != expected)
            throw new SnapshotIntegrityException($"Source observation differs from sealed input: {observationId}.");
        return new(observationId, snapshot.SnapshotId, observation.SourceIdentity, observation.SourceRevision, observation.AcquisitionStatus,
            new VersionedJson(observation.MetadataJson), new SourceArtifact(artifact.ArtifactId, artifact.Length, artifact.Sha256, artifact.RawBytes),
            observation.AcquisitionError);
    }

    private static async Task<List<SourceArtifactRow>> ReadAndValidateArtifactsAsync(ScanContext db, Guid snapshotId,
        IReadOnlyList<SourceObservationRow> observations, CancellationToken token)
    {
        var statuses = observations.ToDictionary(x => x.ObservationId, x => x.AcquisitionStatus);
        var metadata = new List<SourceArtifactRow>();
        await foreach (var artifact in db.SourceArtifacts.AsNoTracking().Where(x => x.SnapshotId == snapshotId).AsAsyncEnumerable().WithCancellation(token))
        {
            SnapshotManifest.ValidateArtifact(artifact, statuses.GetValueOrDefault(artifact.ObservationId, AcquisitionStatus.Unknown));
            metadata.Add(new SourceArtifactRow { ArtifactId = artifact.ArtifactId, SnapshotId = artifact.SnapshotId,
                ObservationId = artifact.ObservationId, Length = artifact.Length, Sha256 = artifact.Sha256 });
        }
        return metadata;
    }

    private static bool SameBytes(byte[]? first, byte[]? second) =>
        first == null ? second == null : second != null && first.AsSpan().SequenceEqual(second);
}
