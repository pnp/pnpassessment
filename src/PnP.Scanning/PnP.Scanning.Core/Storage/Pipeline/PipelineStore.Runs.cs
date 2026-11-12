#nullable enable
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Pipeline.Orchestration;

namespace PnP.Scanning.Core.Storage.Pipeline;

/// <summary>
/// Stores assessment-scoped immutable evidence, phase state, and bound analysis results transactionally.
/// </summary>
internal sealed partial class PipelineStore
{
    /// <summary>Creates foundation rows without changing the existing scanner or its lifecycle.</summary>
    internal Task<bool> CreateCollectionAsync(
        Scan assessment,
        SourceSnapshotRow snapshot,
        PhaseRunRow collection,
        CancellationToken token = default) =>
        WriteAsync(
            snapshot.AssessmentId,
            db =>
            {
                if (assessment.ScanId != snapshot.AssessmentId || snapshot.IsSealed || snapshot.SealedAtUtc != null ||
                    snapshot.ManifestJson != null || snapshot.ManifestDigest != null ||
                    snapshot.MemberIdsJson != null || collection.Kind != PhaseKind.Collection ||
                    collection.Status != ScanStatus.Queued || collection.CompletedRecords != 0 ||
                    collection.TotalRecords < 0 || collection.ParentRunId != null)
                    throw new ArgumentException(
                        "A new collection requires an unsealed snapshot and a queued collection root.");
                _ = SnapshotManifest.Build(snapshot, []);
                ValidatePhase(collection, snapshot);
                db.Scans.Add(assessment);
                db.SourceSnapshots.Add(snapshot);
                db.PhaseRuns.Add(collection);
                return Task.FromResult(true);
            },
            token,
            create: true);

    /// <summary>
    /// Validates a sealed source and persists a queued analysis with pinned rule parameters and manifest.
    /// </summary>
    internal Task<bool> CreateAnalysisAsync(PhaseRunRow phase, CancellationToken token = default) =>
        WriteAsync(
            phase.AssessmentId,
            async db =>
            {
                SourceSnapshotRow? snapshot = await db.SourceSnapshots.SingleAsync(
                    x => x.SnapshotId == phase.SnapshotId,
                    token);
                ValidatePhase(phase, snapshot);
                if (phase.Kind != PhaseKind.Analysis || phase.Status != ScanStatus.Queued ||
                    string.IsNullOrWhiteSpace(phase.RuleVersion) || phase.CompletedRecords != 0)
                    throw new ArgumentException("A new analysis requires a queued phase and an explicit rule version.");
                SnapshotManifest? manifest = await ValidateSnapshotAsync(db, snapshot, token);
                phase.TotalRecords = manifest.Members.Count;
                phase.CheckpointJson = VersionedJson
                    .From(new { stage = "Queued", manifestDigest = snapshot.ManifestDigest })
                    .Json;
                db.PhaseRuns.Add(phase);
                db.AnalysisRuns.Add(
                    new()
                    {
                        AnalysisRunId = phase.RunId,
                        SnapshotId = phase.SnapshotId,
                        ModuleKey = phase.ModuleKey,
                        RuleVersion = phase.RuleVersion,
                        ParametersJson = PipelineRunSettings.AnalysisParameters(phase),
                        ManifestDigest = SnapshotManifest.Digest(manifest.Serialize()),
                        CreatedAtUtc = phase.CreatedAtUtc,
                    });
                return true;
            },
            token);

    private static void ValidatePhase(PhaseRunRow phase, SourceSnapshotRow snapshot)
    {
        if (phase.RunId == Guid.Empty || phase.AssessmentId != snapshot.AssessmentId ||
            phase.SnapshotId != snapshot.SnapshotId || phase.ModuleKey != snapshot.ModuleKey ||
            phase.InputVersion != snapshot.InputVersion || phase.CurrentPhase != phase.Kind)
            throw new ArgumentException("A phase must pin its snapshot identity, module and input version.");
        _ = new VersionedJson(phase.ParametersJson);
        if (phase.CheckpointJson != null) _ = new VersionedJson(phase.CheckpointJson);
    }

    internal Task<bool> SetRunningAsync(Guid assessmentId, Guid runId, CancellationToken token = default) =>
        WriteAsync(
            assessmentId,
            async db =>
            {
                var phase = await db.PhaseRuns.SingleAsync(x => x.RunId == runId, token);
                if (phase.Status is not (ScanStatus.Queued or ScanStatus.Paused or ScanStatus.Terminated))
                    throw new InvalidOperationException($"Cannot start a phase whose status is {phase.Status}.");
                phase.Status = ScanStatus.Running;
                phase.StartedAtUtc ??= DateTime.UtcNow;
                phase.EndedAtUtc = null;
                return true;
            },
            token);

    /// <summary>
    /// Requires complete source results and immutable projection publication before finishing analysis.
    /// </summary>
    internal Task<bool> FinishAnalysisAsync(Guid assessmentId, Guid runId, CancellationToken token = default) =>
        WriteAsync(
            assessmentId,
            async db =>
            {
                PhaseRunRow? phase = await db.PhaseRuns.SingleAsync(x => x.RunId == runId, token);
                if (phase.Kind != PhaseKind.Analysis || phase.Status != ScanStatus.Running)
                    throw new InvalidOperationException("Only a running analysis can finish.");
                PinnedManifest? pinned = await AnalysisManifestAsync(db, runId, token);
                IReadOnlySet<Guid>? completed = await CompletedResultsAsync(db, runId, token);
                if (!completed.SetEquals(pinned.Members.Keys))
                    throw new SnapshotIntegrityException("Analysis has uncommitted source records.");
                _ = await ReadReportsAsync(db, runId, token);
                phase.Status = ScanStatus.Finished;
                phase.EndedAtUtc = DateTime.UtcNow;
                await UpdateParentAsync(db, phase, token);
                return true;
            },
            token);

    private static async Task<IReadOnlySet<Guid>> CompletedResultsAsync(
        ScanContext db,
        Guid runId,
        CancellationToken token)
    {
        var ids = (await db.AnalysisResults.AsNoTracking()
            .Where(x => x.AnalysisRunId == runId)
            .Select(x => x.ObservationId)
            .ToListAsync(token)).ToHashSet();
        var phase = await db.PhaseRuns.AsNoTracking().SingleAsync(x => x.RunId == runId, token);
        if (phase.CompletedRecords != ids.Count)
            throw new SnapshotIntegrityException("Analysis progress differs from committed result membership.");
        return ids;
    }

    /// <summary>
    /// Validates sealed input and rejects changed pinned analysis rule, parameters, or manifest.
    /// </summary>
    private static async Task<PinnedManifest> AnalysisManifestAsync(ScanContext db, Guid runId, CancellationToken token)
    {
        AnalysisRunRow? run = await db.AnalysisRuns.AsNoTracking().SingleAsync(x => x.AnalysisRunId == runId, token);
        PhaseRunRow? phase = await db.PhaseRuns.AsNoTracking().SingleAsync(x => x.RunId == runId, token);
        if (run.RuleVersion != phase.RuleVersion || run.ParametersJson != PipelineRunSettings.AnalysisParameters(phase))
            throw new SnapshotIntegrityException("The analysis run's pinned rule or parameters changed.");
        SourceSnapshotRow? snapshot =
            await db.SourceSnapshots.AsNoTracking().SingleAsync(x => x.SnapshotId == run.SnapshotId, token);
        PinnedManifest? pinned = new PinnedManifest(await ValidateSnapshotAsync(db, snapshot, token));
        if (pinned.Digest != run.ManifestDigest || run.ModuleKey != snapshot.ModuleKey)
            throw new SnapshotIntegrityException("The analysis run's pinned input differs from its selected snapshot.");
        return pinned;
    }

    /// <summary>
    /// Appends one validated source result and progress checkpoint while rejecting changed replay.
    /// </summary>
    private static async Task<bool> CommitResultAsync(
        ScanContext db,
        Guid runId,
        AnalysisResult result,
        PinnedManifest pinned,
        CancellationToken token)
    {
        if (!Enum.IsDefined(result.Outcome) || string.IsNullOrWhiteSpace(result.Reason))
            throw new ArgumentException("Analysis results require an explicit outcome and reason.");
        PhaseRunRow? phase = await db.PhaseRuns.SingleAsync(x => x.RunId == runId, token);
        if (phase.Kind != PhaseKind.Analysis || phase.Status != ScanStatus.Running)
            throw new InvalidOperationException("The writer requires a running analysis phase.");
        AnalysisRunRow? run = await db.AnalysisRuns.SingleAsync(x => x.AnalysisRunId == runId, token);
        if (pinned.Digest != run.ManifestDigest)
            throw new SnapshotIntegrityException("The analysis input digest changed.");
        SourceSnapshotRow? snapshot = await db.SourceSnapshots.SingleAsync(x => x.SnapshotId == run.SnapshotId, token);
        await ReadSelectedObservationAsync(db, snapshot, result.ObservationId, pinned, token);
        AnalysisResultRow? previous = await db.AnalysisResults.SingleOrDefaultAsync(
            x => x.AnalysisRunId == runId && x.ObservationId == result.ObservationId,
            token);
        if (previous != null)
        {
            if (previous.Outcome != result.Outcome || previous.Reason != result.Reason ||
                previous.PayloadJson != result.Payload.Json)
                throw new SnapshotIntegrityException("Cannot replace a committed result; create a new analysis run.");
            return false;
        }

        if (run.ReportDigest != null)
            throw new InvalidOperationException("Results cannot be appended after report publication.");
        db.AnalysisResults.Add(
            new()
            {
                AnalysisRunId = runId,
                ObservationId = result.ObservationId,
                SnapshotId = run.SnapshotId,
                Outcome = result.Outcome,
                Reason = result.Reason,
                PayloadJson = result.Payload.Json,
                CommittedAtUtc = DateTime.UtcNow,
            });
        phase.CompletedRecords++;
        if (result.Outcome is AnalysisOutcome.Unknown or AnalysisOutcome.Failed)
        {
            phase.ErrorCount++;
            phase.LastError = $"{result.Outcome}: {result.Reason}";
        }

        phase.CheckpointJson = VersionedJson.From(
                new { stage = "Analysis", result.ObservationId, committedResults = phase.CompletedRecords })
            .Json;
        await UpdateParentAsync(db, phase, token);
        return true;
    }
}