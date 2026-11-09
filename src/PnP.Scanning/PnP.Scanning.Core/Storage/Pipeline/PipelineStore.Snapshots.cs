#nullable enable
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;

namespace PnP.Scanning.Core.Storage.Pipeline;

/// <summary>
/// Stores assessment-scoped immutable evidence, phase state, and bound analysis results transactionally.
/// </summary>
internal sealed partial class PipelineStore
{
    /// <summary>
    /// Commits original source evidence and the collector checkpoint together, accepting only identical replay.
    /// </summary>
    private Task<bool> CommitObservationAsync(
        Guid assessmentId,
        Guid snapshotId,
        Guid runId,
        CollectionRecord record,
        CancellationToken token) =>
        WriteAsync(
            assessmentId,
            async db =>
            {
                SourceSnapshotRow? snapshot = await db.SourceSnapshots.SingleAsync(
                    x => x.SnapshotId == snapshotId,
                    token);
                PhaseRunRow? run = await db.PhaseRuns.SingleAsync(x => x.RunId == runId, token);
                if (snapshot.IsSealed || run.SnapshotId != snapshotId || run.Kind != PhaseKind.Collection ||
                    run.Status != ScanStatus.Running)
                    throw new InvalidOperationException(
                        "The writer requires its bound, running collection and an unsealed snapshot.");
                CollectedObservation? item = record.Observation;
                if (item != null)
                {
                    if (item.ObservationId == Guid.Empty || string.IsNullOrWhiteSpace(item.SourceIdentity) ||
                        !Enum.IsDefined(item.AcquisitionStatus))
                        throw new ArgumentException(
                            "A source observation requires an ID, identity and acquisition status.");
                    byte[]? bytes = item.RawBytes?.ToArray();
                    SourceObservationRow? row = new SourceObservationRow
                    {
                        ObservationId = item.ObservationId,
                        SnapshotId = snapshotId,
                        SourceIdentity = item.SourceIdentity,
                        SourceRevision = item.SourceRevision,
                        AcquisitionStatus = item.AcquisitionStatus,
                        MetadataJson = item.Metadata.Json,
                        AcquisitionError = item.AcquisitionError,
                        RawBytes = bytes,
                        Length = bytes?.LongLength,
                        Sha256 = bytes == null ? null : SnapshotManifest.Digest(bytes),
                    };
                    SnapshotManifest.ValidateContent(row);
                    SourceObservationRow? previous = await db.SourceObservations.SingleOrDefaultAsync(
                        x => x.ObservationId == item.ObservationId,
                        token);
                    if (previous != null)
                    {
                        SnapshotManifest.ValidateContent(previous);
                        if (previous.SnapshotId != row.SnapshotId || previous.SourceIdentity != row.SourceIdentity ||
                            previous.SourceRevision != row.SourceRevision ||
                            previous.AcquisitionStatus != row.AcquisitionStatus ||
                            previous.MetadataJson != row.MetadataJson ||
                            previous.AcquisitionError != row.AcquisitionError || previous.Length != row.Length ||
                            previous.Sha256 != row.Sha256)
                            throw new SnapshotIntegrityException(
                                "Replay attempted to replace an existing source observation.");
                    }
                    else
                    {
                        db.SourceObservations.Add(row);
                        run.CompletedRecords++;
                        if (item.AcquisitionStatus != AcquisitionStatus.Complete)
                        {
                            run.ErrorCount++;
                            run.LastError =
                                $"{item.AcquisitionStatus}: {item.AcquisitionError ?? "Content acquisition was not complete."}";
                        }
                    }
                }

                if (record.ExpectedRecords is int expected)
                {
                    if (expected < run.CompletedRecords)
                        throw new ArgumentException("Expected records are less than committed records.");
                    run.TotalRecords = expected;
                }

                run.CheckpointJson = record.Checkpoint.Json;
                await UpdateParentAsync(db, run, token);
                return true;
            },
            token);

    /// <summary>
    /// Validates source membership and content before atomically sealing collection and advancing its parent.
    /// </summary>
    private Task<SnapshotInfo> SealAsync(Guid assessmentId, Guid snapshotId, Guid runId, CancellationToken token) =>
        WriteAsync(
            assessmentId,
            async db =>
            {
                SourceSnapshotRow? snapshot = await db.SourceSnapshots.SingleAsync(
                    x => x.SnapshotId == snapshotId,
                    token);
                PhaseRunRow? run = await db.PhaseRuns.SingleAsync(x => x.RunId == runId, token);
                if (run.Kind != PhaseKind.Collection || run.SnapshotId != snapshotId)
                    throw new InvalidOperationException("Only the bound collection can seal this snapshot.");
                if (snapshot.IsSealed) return (await ValidateSnapshotAsync(db, snapshot, token)).Info();
                if (run.Status != ScanStatus.Running)
                    throw new InvalidOperationException("Only a running collection can seal its snapshot.");
                List<SourceObservationRow>?
                    observations = await ReadAndValidateObservationsAsync(db, snapshotId, token);
                if (run.CompletedRecords != observations.Count ||
                    (run.TotalRecords > 0 && run.TotalRecords != observations.Count))
                    throw new SnapshotIntegrityException(
                        "Collection progress does not match committed snapshot membership.");
                SnapshotManifest? manifest = SnapshotManifest.Build(snapshot, observations, contentVerified: true);
                snapshot.ManifestJson = manifest.Serialize();
                snapshot.MemberIdsJson = manifest.MemberIds();
                snapshot.ManifestDigest = SnapshotManifest.Digest(snapshot.ManifestJson);
                snapshot.SealedAtUtc = DateTime.UtcNow;
                snapshot.IsSealed = true;
                run.TotalRecords = observations.Count;
                run.Status = ScanStatus.Finished;
                run.EndedAtUtc = snapshot.SealedAtUtc;
                run.CheckpointJson = VersionedJson.From(
                        new { stage = "Sealed", snapshotId, manifestDigest = snapshot.ManifestDigest })
                    .Json;
                if (run.ParentRunId is Guid parentId)
                {
                    PhaseRunRow? parent = await db.PhaseRuns.SingleAsync(x => x.RunId == parentId, token);
                    parent.CurrentPhase = PhaseKind.Analysis;
                    parent.CompletedRecords = 0;
                    parent.TotalRecords = observations.Count;
                    parent.CheckpointJson = run.CheckpointJson;
                }

                return manifest.Info();
            },
            token);

    private async Task<SnapshotManifest> OpenManifestAsync(Guid assessmentId, Guid snapshotId, CancellationToken token)
    {
        using var db = CreateContext(assessmentId, readOnly: true);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var snapshot =
            await db.SourceSnapshots.AsNoTracking()
                .SingleOrDefaultAsync(x => x.SnapshotId == snapshotId && x.AssessmentId == assessmentId, token) ??
            throw new KeyNotFoundException("The selected source snapshot does not exist.");
        return await ValidateSnapshotAsync(db, snapshot, token);
    }

    private static async Task<SnapshotManifest> ValidateSnapshotAsync(
        ScanContext db,
        SourceSnapshotRow snapshot,
        CancellationToken token)
    {
        if (!snapshot.IsSealed || snapshot.SealedAtUtc == null || snapshot.ManifestJson == null ||
            snapshot.MemberIdsJson == null || snapshot.ManifestDigest == null)
            throw new SnapshotIntegrityException("Analysis requires an explicitly selected, sealed snapshot.");
        if (SnapshotManifest.Digest(snapshot.ManifestJson) != snapshot.ManifestDigest)
            throw new SnapshotIntegrityException("Snapshot manifest SHA-256 mismatch.");
        var observations = await ReadAndValidateObservationsAsync(db, snapshot.SnapshotId, token);
        var actual = SnapshotManifest.Build(snapshot, observations, contentVerified: true);
        if (actual.Serialize() != snapshot.ManifestJson || actual.MemberIds() != snapshot.MemberIdsJson)
            throw new SnapshotIntegrityException(
                "Snapshot membership or source evidence differs from its sealed manifest.");
        return actual;
    }

    private static async Task<SourceRecord> ReadSelectedObservationAsync(
        ScanContext db,
        SourceSnapshotRow snapshot,
        Guid observationId,
        PinnedManifest pinned,
        CancellationToken token)
    {
        if (!snapshot.IsSealed || snapshot.SealedAtUtc == null || snapshot.ManifestJson != pinned.Json ||
            snapshot.ManifestDigest != pinned.Digest || snapshot.MemberIdsJson != pinned.MemberIdsJson)
            throw new SnapshotIntegrityException("The pinned snapshot seal or manifest changed.");
        if (!pinned.Members.TryGetValue(observationId, out var expected))
            throw new SnapshotIntegrityException("The requested observation is not a member of the selected snapshot.");
        var observation =
            await db.SourceObservations.AsNoTracking()
                .SingleOrDefaultAsync(x => x.ObservationId == observationId, token) ??
            throw new SnapshotIntegrityException($"Missing source observation {observationId}.");
        var actual = SnapshotManifest.Build(snapshot, [observation]);
        var manifest = pinned.Manifest;
        if (actual.FormatVersion != manifest.FormatVersion || actual.AssessmentId != manifest.AssessmentId ||
            actual.SnapshotId != manifest.SnapshotId || actual.ModuleKey != manifest.ModuleKey ||
            actual.InputVersion != manifest.InputVersion || actual.ScopeJson != manifest.ScopeJson ||
            actual.Members.Single() != expected)
            throw new SnapshotIntegrityException($"Source observation differs from sealed input: {observationId}.");
        return new(
            observationId,
            snapshot.SnapshotId,
            observation.SourceIdentity,
            observation.SourceRevision,
            observation.AcquisitionStatus,
            new VersionedJson(observation.MetadataJson),
            new SourceArtifact(observation.Length, observation.Sha256, observation.RawBytes),
            observation.AcquisitionError);
    }

    private static async Task<List<SourceObservationRow>> ReadAndValidateObservationsAsync(
        ScanContext db,
        Guid snapshotId,
        CancellationToken token)
    {
        var rows = new List<SourceObservationRow>();
        await foreach (var row in db.SourceObservations.AsNoTracking()
                           .Where(x => x.SnapshotId == snapshotId)
                           .AsAsyncEnumerable()
                           .WithCancellation(token))
        {
            SnapshotManifest.ValidateContent(row);
            // Only metadata is retained after validating each BLOB.
            row.RawBytes = null;
            rows.Add(row);
        }

        return rows;
    }
}