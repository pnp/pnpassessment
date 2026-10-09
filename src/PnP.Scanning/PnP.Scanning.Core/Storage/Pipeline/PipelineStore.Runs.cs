#nullable enable
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Pipeline.Contracts;
using PnP.Scanning.Core.Services;

namespace PnP.Scanning.Core.Storage.Pipeline;

internal sealed partial class PipelineStore
{
    internal Task<bool> CreateCollectionAsync(SourceSnapshotRow snapshot, PhaseRunRow root,
        PhaseRunRow? collectionChild, StartRequest protectedOptions, CancellationToken token) =>
        WriteAsync(snapshot.AssessmentId, db =>
        {
            db.Scans.Add(new Scan
            {
                ScanId = snapshot.AssessmentId, CLIMode = $"Pipeline:{snapshot.ModuleKey}",
                StartDate = DateTime.UtcNow, Status = ScanStatus.Queued, Version = VersionManager.GetCurrentVersion(),
                PreScanStatus = SiteWebStatus.Queued, PostScanStatus = SiteWebStatus.Queued,
                CLITenant = protectedOptions.Tenant, CLITenantId = protectedOptions.TenantId,
                CLIEnvironment = protectedOptions.Environment, CLISiteList = protectedOptions.SitesList,
                CLISiteFile = protectedOptions.SitesFile, CLIAuthMode = protectedOptions.AuthMode,
                CLIApplicationId = protectedOptions.ApplicationId, CLICertPath = protectedOptions.CertPath,
                CLICertFile = protectedOptions.CertFile, CLICertFilePassword = protectedOptions.CertPassword,
                CLIThreads = root.Threads,
            });
            db.SourceSnapshots.Add(snapshot);
            db.Properties.AddRange(protectedOptions.Properties.Select(x => new Property
            { ScanId = snapshot.AssessmentId, Name = x.Property, Type = x.Type, Value = x.Value }));
            db.PhaseRuns.Add(root);
            if (collectionChild != null) db.PhaseRuns.Add(collectionChild);
            return Task.FromResult(true);
        }, token, create: true);

    internal Task<bool> CreateAnalysisAsync(PhaseRunRow root, CancellationToken token) =>
        WriteAsync(root.AssessmentId, async db =>
        {
            var snapshot = await db.SourceSnapshots.SingleAsync(x => x.SnapshotId == root.SnapshotId, token);
            var manifest = await ValidateSnapshotAsync(db, snapshot, token);
            root.TotalRecords = manifest.Members.Count;
            db.PhaseRuns.Add(root);
            root.CheckpointJson = VersionedJson.From(new { stage = "Queued", snapshotId = root.SnapshotId, manifestDigest = snapshot.ManifestDigest }).Json;
            db.AnalysisRuns.Add(AnalysisRow(root, snapshot.ManifestDigest!));
            await UpdateAssessmentAsync(db, root.AssessmentId, ScanStatus.Queued);
            return true;
        }, token);

    internal Task<PhaseRunRow> PrepareAnalysisChildAsync(PhaseRunRow root, CancellationToken token) =>
        WriteAsync(root.AssessmentId, async db =>
        {
            if (root.Kind == PhaseKind.Analysis)
                return await db.PhaseRuns.SingleAsync(x => x.RunId == root.RunId, token);
            var existing = await db.PhaseRuns.SingleOrDefaultAsync(x => x.RunId == root.AnalysisRunId, token);
            if (existing != null) return existing;
            var snapshot = await db.SourceSnapshots.SingleAsync(x => x.SnapshotId == root.SnapshotId, token);
            var manifest = await ValidateSnapshotAsync(db, snapshot, token);
            var savedRoot = await db.PhaseRuns.SingleAsync(x => x.RunId == root.RunId, token);
            if (savedRoot.CheckpointJson == null || new VersionedJson(savedRoot.CheckpointJson).Value.GetProperty("manifestDigest").GetString() != snapshot.ManifestDigest)
                throw new SnapshotIntegrityException("The collection's sealed input changed before analysis was created.");
            var child = new PhaseRunRow
            {
                RunId = root.AnalysisRunId ?? throw new InvalidOperationException("Pipeline has no reserved analysis ID."),
                ParentRunId = root.RunId, AssessmentId = root.AssessmentId,
                Kind = PhaseKind.Analysis, CurrentPhase = PhaseKind.Analysis, Status = ScanStatus.Queued,
                ModuleKey = root.ModuleKey, InputVersion = root.InputVersion, SnapshotId = root.SnapshotId,
                AnalysisRunId = root.AnalysisRunId, RuleVersion = root.RuleVersion,
                ParametersJson = root.AnalysisParametersJson, AnalysisParametersJson = root.AnalysisParametersJson,
                Threads = root.Threads, TotalRecords = manifest.Members.Count, CreatedAtUtc = DateTime.UtcNow,
            };
            db.PhaseRuns.Add(child);
            db.AnalysisRuns.Add(AnalysisRow(child, snapshot.ManifestDigest!));
            return child;
        }, token);

    private static AnalysisRunRow AnalysisRow(PhaseRunRow phase, string manifestDigest) => new()
    {
        AnalysisRunId = phase.RunId, SnapshotId = phase.SnapshotId, ModuleKey = phase.ModuleKey,
        RuleVersion = phase.RuleVersion ?? throw new InvalidOperationException("An analysis rule must be pinned before enqueueing."),
        ParametersJson = phase.AnalysisParametersJson, CreatedAtUtc = DateTime.UtcNow,
        ManifestDigest = manifestDigest,
    };

    internal Task<bool> SetRunningAsync(Guid assessmentId, Guid rootId, Guid phaseId, CancellationToken token) =>
        WriteAsync(assessmentId, async db =>
        {
            var root = await db.PhaseRuns.SingleAsync(x => x.RunId == rootId, token);
            var phase = phaseId == rootId ? root : await db.PhaseRuns.SingleAsync(x => x.RunId == phaseId, token);
            if (phase.Status == ScanStatus.Finished) throw new InvalidOperationException("Cannot rerun a finished phase.");
            root.Status = phase.Status = ScanStatus.Running;
            root.CurrentPhase = phase.Kind;
            root.StartedAtUtc ??= DateTime.UtcNow;
            phase.StartedAtUtc ??= DateTime.UtcNow;
            root.EndedAtUtc = phase.EndedAtUtc = null;
            await UpdateAssessmentAsync(db, assessmentId, ScanStatus.Running);
            return true;
        }, token);

    internal Task<bool> QueueRestartAsync(Guid assessmentId, Guid rootId, int threads, CancellationToken token) =>
        WriteAsync(assessmentId, async db =>
        {
            var root = await db.PhaseRuns.SingleAsync(x => x.RunId == rootId, token);
            if (root.Status is not (ScanStatus.Paused or ScanStatus.Terminated))
                throw new InvalidOperationException($"Cannot restart a run whose status is {root.Status}.");
            root.Status = ScanStatus.Queued;
            root.EndedAtUtc = null;
            if (threads > 0) root.Threads = threads;
            var children = await db.PhaseRuns.Where(x => x.ParentRunId == rootId && x.Status != ScanStatus.Finished).ToListAsync(token);
            foreach (var child in children)
            {
                child.Status = ScanStatus.Queued;
                child.EndedAtUtc = null;
                child.Threads = root.Threads;
            }
            await UpdateAssessmentAsync(db, assessmentId, ScanStatus.Queued);
            return true;
        }, token);

    internal Task<bool> SetPausingAsync(Guid assessmentId, Guid rootId, CancellationToken token) =>
        WriteAsync(assessmentId, async db =>
        {
            var runs = await db.PhaseRuns.Where(x => x.RunId == rootId || x.ParentRunId == rootId).ToListAsync(token);
            foreach (var run in runs.Where(x => x.Status is ScanStatus.Running or ScanStatus.Queued)) run.Status = ScanStatus.Pausing;
            await UpdateAssessmentAsync(db, assessmentId, ScanStatus.Pausing);
            return true;
        }, token);

    internal Task<bool> FinishAnalysisAsync(Guid assessmentId, Guid analysisRunId, CancellationToken token) =>
        WriteAsync(assessmentId, async db =>
        {
            var phase = await db.PhaseRuns.SingleAsync(x => x.RunId == analysisRunId, token);
            var snapshot = await db.SourceSnapshots.SingleAsync(x => x.SnapshotId == phase.SnapshotId, token);
            var manifest = await ValidateSnapshotAsync(db, snapshot, token);
            var count = await db.AnalysisResults.CountAsync(x => x.AnalysisRunId == analysisRunId, token);
            if (count != manifest.Members.Count || phase.CompletedRecords != count)
                throw new SnapshotIntegrityException("Analysis has uncommitted source records and cannot finish.");
            string? reportDigest = null;
            if (phase.ModuleKey == "classicpage")
            {
                var checkpoint = new VersionedJson(phase.CheckpointJson!).Value;
                if (!checkpoint.TryGetProperty("reportDigest", out var digest))
                    throw new SnapshotIntegrityException("Classic Page analysis has not published its report projections.");
                reportDigest = digest.GetString();
                var rows = await db.ClassicPageReportRows.Where(x => x.AnalysisRunId == analysisRunId).ToListAsync(token);
                if (rows.Count == 0 || AnalysisReportDigest.Compute(rows.Select(x => new AnalysisReportRow(x.Kind, x.RowKey, x.Ordinal, new VersionedJson(x.PayloadJson)))) != reportDigest)
                    throw new SnapshotIntegrityException("Published report digest differs from its checkpoint.");
            }
            phase.Status = ScanStatus.Finished;
            phase.EndedAtUtc = DateTime.UtcNow;
            phase.CheckpointJson = VersionedJson.From(new { stage = "Finished", snapshotId = phase.SnapshotId, committedResults = count, reportDigest }).Json;
            await UpdateParentAsync(db, phase);
            return true;
        }, token);

    internal Task<bool> SettleRootAsync(Guid assessmentId, Guid rootId, ScanStatus status, string? error = null,
        CancellationToken token = default) => WriteAsync(assessmentId, async db =>
    {
        var root = await db.PhaseRuns.SingleAsync(x => x.RunId == rootId, token);
        var children = await db.PhaseRuns.Where(x => x.ParentRunId == rootId).ToListAsync(token);
        if (status == ScanStatus.Finished && children.Any(x => x.Status != ScanStatus.Finished))
            throw new InvalidOperationException("Cannot finish a pipeline with unfinished child phases.");
        root.Status = status;
        root.EndedAtUtc = DateTime.UtcNow;
        if (error != null) AddError(root, error);
        foreach (var child in children.Where(x => x.Status != ScanStatus.Finished))
        {
            child.Status = status;
            child.EndedAtUtc = root.EndedAtUtc;
            if (error != null) AddError(child, error);
        }
        // Never call the legacy EndScanAsync, which forcibly changes failures into Finished.
        await UpdateAssessmentAsync(db, assessmentId, status);
        return true;
    }, token);

    internal async Task MarkInterruptedRunsAsync(CancellationToken token)
    {
        foreach (var root in await ListRootsAsync(token))
            if (root.Status is ScanStatus.Queued or ScanStatus.Running or ScanStatus.Pausing)
                await SettleRootAsync(root.AssessmentId, root.RunId, ScanStatus.Terminated,
                    "Engine stopped before phase settlement; resume the original run and checkpoint.", token);
    }

    private async Task<IReadOnlySet<Guid>> CompletedResultsAsync(Guid assessmentId, Guid analysisRunId, CancellationToken token)
    {
        await EnsureDatabaseAsync(assessmentId, token);
        using var db = CreateContext(assessmentId);
        var completed = (await db.AnalysisResults.AsNoTracking().Where(x => x.AnalysisRunId == analysisRunId)
            .Select(x => x.ObservationId).ToListAsync(token)).ToHashSet();
        var phase = await db.PhaseRuns.AsNoTracking().SingleAsync(x => x.RunId == analysisRunId, token);
        if (phase.CompletedRecords != completed.Count)
            throw new SnapshotIntegrityException("Analysis checkpoint differs from committed result membership.");
        return completed;
    }

    private async Task<PinnedManifest> AnalysisManifestAsync(Guid assessmentId, Guid analysisRunId, CancellationToken token)
    {
        await EnsureDatabaseAsync(assessmentId, token);
        using var db = CreateContext(assessmentId);
        var run = await db.AnalysisRuns.AsNoTracking().SingleAsync(x => x.AnalysisRunId == analysisRunId, token);
        var manifest = new PinnedManifest(await OpenManifestAsync(assessmentId, run.SnapshotId, token));
        if (manifest.Digest != run.ManifestDigest)
            throw new SnapshotIntegrityException("The analysis run's pinned manifest digest differs from its selected input.");
        return manifest;
    }

    private Task<bool> CommitResultAsync(Guid assessmentId, Guid analysisRunId, AnalysisResult result, PinnedManifest pinned, CancellationToken token) =>
        WriteAsync(assessmentId, async db =>
        {
            if (!Enum.IsDefined(result.Outcome) || string.IsNullOrWhiteSpace(result.Reason))
                throw new ArgumentException("Analysis results require an explicit outcome and reason.");
            var phase = await db.PhaseRuns.SingleAsync(x => x.RunId == analysisRunId, token);
            if (phase.Kind != PhaseKind.Analysis || phase.Status != ScanStatus.Running)
                throw new InvalidOperationException("Result writer is not bound to a running analysis phase.");
            var run = await db.AnalysisRuns.SingleAsync(x => x.AnalysisRunId == analysisRunId, token);
            var snapshot = await db.SourceSnapshots.SingleAsync(x => x.SnapshotId == run.SnapshotId, token);
            if (pinned.Digest != run.ManifestDigest) throw new SnapshotIntegrityException("Analysis input digest changed.");
            // Validate this saved source again after module execution, without rereading every BLOB for every result.
            await ReadSelectedObservationAsync(db, snapshot, result.ObservationId, pinned, token);
            var previous = await db.AnalysisResults.SingleOrDefaultAsync(x =>
                x.AnalysisRunId == analysisRunId && x.ObservationId == result.ObservationId, token);
            if (previous != null)
            {
                if (previous.Outcome != result.Outcome || previous.Reason != result.Reason || previous.PayloadJson != result.Payload.Json)
                    throw new InvalidOperationException("Cannot replace a committed result; request a new analyze run instead.");
                return false;
            }
            db.AnalysisResults.Add(new()
            {
                AnalysisRunId = analysisRunId, ObservationId = result.ObservationId, SnapshotId = run.SnapshotId,
                Outcome = result.Outcome, Reason = result.Reason, PayloadJson = result.Payload.Json, CommittedAtUtc = DateTime.UtcNow,
            });
            phase.CompletedRecords++;
            var error = result.Outcome is AnalysisOutcome.Unknown or AnalysisOutcome.Failed ? $"{result.Outcome}: {result.Reason}" : null;
            if (error != null) AddError(phase, error, result.ObservationId);
            phase.CheckpointJson = VersionedJson.From(new
            {
                stage = "Analysis", analysisRunId, snapshotId = run.SnapshotId,
                lastCommittedObservationId = result.ObservationId, committedResults = phase.CompletedRecords,
                resumeFrom = "AnalysisResults primary key membership",
            }).Json;
            await UpdateParentAsync(db, phase, error, result.ObservationId);
            return true;
        }, token);
}
