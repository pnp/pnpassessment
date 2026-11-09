#nullable enable
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Pipeline.Collection.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Pipeline.Orchestration;

namespace PnP.Scanning.Core.Storage.Pipeline;

internal sealed partial class PipelineStore
{
    internal async Task<SnapshotInfo> OpenSnapshotAsync(Guid assessmentId, Guid snapshotId, CancellationToken token = default) =>
        (await OpenManifestAsync(assessmentId, snapshotId, token)).Info();

    internal async Task EnsureDatabaseAsync(Guid assessmentId, CancellationToken token)
    {
        if (migrated.ContainsKey(assessmentId)) return;
        await WriteAsync(assessmentId, _ => Task.FromResult(true), token);
    }

    internal async Task<PhaseRunRow?> LatestRootAsync(Guid assessmentId, CancellationToken token = default) =>
        (await GetRunsAsync(assessmentId, token)).LastOrDefault(x => x.ParentRunId == null);

    internal Task<bool> FinishResultsAsync(Guid assessmentId, Guid runId, CancellationToken token) =>
        WriteAsync(assessmentId, async db =>
        {
            var phase = await db.PhaseRuns.SingleAsync(x => x.RunId == runId, token);
            if (phase.Kind != PhaseKind.Analysis || phase.Status != ScanStatus.Running)
                throw new InvalidOperationException("Only a running analysis can finish.");
            var manifest = await AnalysisManifestAsync(db, runId, token);
            var completed = await CompletedResultsAsync(db, runId, token);
            if (!completed.SetEquals(manifest.Members.Keys))
                throw new SnapshotIntegrityException("Analysis has uncommitted source records.");
            phase.Status = ScanStatus.Finished;
            phase.EndedAtUtc = DateTime.UtcNow;
            await UpdateParentAsync(db, phase, token);
            return true;
        }, token);

    internal IEnumerable<Guid> AssessmentsOnDisk() => !Directory.Exists(RootDirectory) ? [] :
        Directory.EnumerateDirectories(RootDirectory).Select(Path.GetFileName)
            .Where(name => Guid.TryParse(name, out _)).Select(name => Guid.Parse(name!))
            .Where(id => File.Exists(DatabasePath(id)));

    internal async Task<IReadOnlyList<PhaseRunRow>> ListRootsAsync(CancellationToken token = default)
    {
        var result = new List<PhaseRunRow>();
        foreach (var id in AssessmentsOnDisk())
        {
            await EnsureDatabaseAsync(id, token);
            result.AddRange((await GetRunsAsync(id, token)).Where(x => x.ParentRunId == null));
        }
        return result.OrderBy(x => x.CreatedAtUtc).ToArray();
    }

    internal Task<bool> CreateCollectionAsync(Scan assessment, SourceSnapshotRow snapshot,
        PhaseRunRow root, PhaseRunRow? collectionChild, CancellationToken token, bool adoptLegacy = false) =>
        WriteAsync(snapshot.AssessmentId, async db =>
        {
            if (collectionChild == null)
            {
                if (root.Kind != PhaseKind.Collection) throw new ArgumentException("Collection root is required.");
                ValidatePhase(root, snapshot);
            }
            else
            {
                ValidatePhase(collectionChild, snapshot);
                if (root.Kind != PhaseKind.Pipeline || root.CurrentPhase != PhaseKind.Collection ||
                    root.RunId == Guid.Empty || root.AssessmentId != assessment.ScanId ||
                    root.AssessmentId != snapshot.AssessmentId || root.SnapshotId != snapshot.SnapshotId ||
                    root.ModuleKey != snapshot.ModuleKey || root.InputVersion != snapshot.InputVersion ||
                    collectionChild.ParentRunId != root.RunId || collectionChild.Kind != PhaseKind.Collection)
                    throw new ArgumentException("Pipeline phases must share the selected assessment and snapshot.");
            }
            if (assessment.ScanId != snapshot.AssessmentId || snapshot.IsSealed ||
                snapshot.SealedAtUtc != null || snapshot.ManifestJson != null || snapshot.ManifestDigest != null ||
                snapshot.MemberIdsJson != null || root.Status != ScanStatus.Queued ||
                root.ParentRunId != null || root.CompletedRecords != 0 ||
                collectionChild is { Status: not ScanStatus.Queued })
                throw new ArgumentException("New collection requires an unsealed input and queued phases.");
            _ = SnapshotManifest.Build(snapshot, []);
            _ = PipelineRunSettings.Read(root);
            if (adoptLegacy)
            {
                var previous = await db.Scans.SingleAsync(x => x.ScanId == assessment.ScanId, token);
                if (previous.Status is not (ScanStatus.Paused or ScanStatus.Terminated) ||
                    await db.PhaseRuns.AnyAsync(x => x.AssessmentId == assessment.ScanId, token))
                    throw new InvalidOperationException("Historical Classic assessment cannot be adopted in its current state.");
                previous.Status = ScanStatus.Queued;
                previous.EndDate = default;
                await db.Properties.Where(x => x.ScanId == assessment.ScanId).ExecuteDeleteAsync(token);
                db.History.Add(new History
                {
                    ScanId = assessment.ScanId, EventDate = DateTime.UtcNow, Event = Constants.EventAssessmentStatusChange,
                    Message = "Restart is acquiring a versioned source snapshot using the current Classic implementation.",
                });
            }
            else db.Scans.Add(assessment);
            var scope = new VersionedJson(snapshot.ScopeJson).Value;
            if (scope.ValueKind == System.Text.Json.JsonValueKind.Object && scope.TryGetProperty("properties", out var properties))
                foreach (var property in properties.EnumerateArray())
                    db.Properties.Add(new Property
                    {
                        ScanId = assessment.ScanId, Name = property.GetProperty("Property").GetString(),
                        Type = property.GetProperty("Type").GetString(), Value = property.GetProperty("Value").GetString(),
                    });
            db.SourceSnapshots.Add(snapshot);
            db.PhaseRuns.Add(root);
            if (collectionChild != null) db.PhaseRuns.Add(collectionChild);
            return true;
        }, token, create: true);

    internal Task<bool> SetRunningAsync(Guid assessmentId, Guid rootId, Guid phaseId, CancellationToken token) =>
        WriteAsync(assessmentId, async db =>
        {
            var root = await db.PhaseRuns.SingleAsync(x => x.RunId == rootId, token);
            var phase = rootId == phaseId ? root : await db.PhaseRuns.SingleAsync(x => x.RunId == phaseId, token);
            if (phaseId != rootId && phase.ParentRunId != rootId ||
                root.Status is not (ScanStatus.Queued or ScanStatus.Running) ||
                phase.Status != ScanStatus.Queued)
                throw new InvalidOperationException("Only a queued phase of the selected root can start.");
            root.Status = phase.Status = ScanStatus.Running;
            root.CurrentPhase = phase.Kind;
            root.StartedAtUtc ??= DateTime.UtcNow;
            phase.StartedAtUtc ??= DateTime.UtcNow;
            root.EndedAtUtc = phase.EndedAtUtc = null;
            await UpdateAssessmentAsync(db, assessmentId, ScanStatus.Running, token);
            return true;
        }, token);

    internal Task<PhaseRunRow> PrepareAnalysisChildAsync(PhaseRunRow root, CancellationToken token) =>
        WriteAsync(root.AssessmentId, async db =>
        {
            if (root.Kind == PhaseKind.Analysis)
                return await db.PhaseRuns.SingleAsync(x => x.RunId == root.RunId, token);
            var settings = PipelineRunSettings.Read(root);
            var id = settings.AnalysisRunId ?? throw new InvalidOperationException("Pipeline analysis identity is absent.");
            var existing = await db.PhaseRuns.SingleOrDefaultAsync(x => x.RunId == id, token);
            if (existing != null) return existing;
            var snapshot = await db.SourceSnapshots.SingleAsync(x => x.SnapshotId == root.SnapshotId, token);
            var manifest = await ValidateSnapshotAsync(db, snapshot, token);
            var savedRoot = await db.PhaseRuns.SingleAsync(x => x.RunId == root.RunId, token);
            if (savedRoot.CurrentPhase != PhaseKind.Analysis || savedRoot.CheckpointJson == null ||
                new VersionedJson(savedRoot.CheckpointJson).Value.GetProperty("manifestDigest").GetString() != snapshot.ManifestDigest)
                throw new SnapshotIntegrityException("Sealed collection differs from the pipeline checkpoint.");
            var child = new PhaseRunRow
            {
                RunId = id, ParentRunId = root.RunId, AssessmentId = root.AssessmentId,
                Kind = PhaseKind.Analysis, CurrentPhase = PhaseKind.Analysis, Status = ScanStatus.Queued,
                ModuleKey = root.ModuleKey, InputVersion = root.InputVersion, SnapshotId = root.SnapshotId,
                RuleVersion = root.RuleVersion, ParametersJson = settings.Serialize(),
                TotalRecords = manifest.Members.Count, CreatedAtUtc = DateTime.UtcNow,
            };
            db.PhaseRuns.Add(child);
            db.AnalysisRuns.Add(new()
            {
                AnalysisRunId = id, SnapshotId = root.SnapshotId, ModuleKey = root.ModuleKey,
                RuleVersion = root.RuleVersion ?? throw new InvalidOperationException("Analysis rule is not pinned."),
                ParametersJson = settings.AnalysisParametersJson, ManifestDigest = snapshot.ManifestDigest!,
                CreatedAtUtc = child.CreatedAtUtc,
            });
            return child;
        }, token);

    internal Task<bool> QueueRestartAsync(Guid assessmentId, Guid rootId, int threads, CancellationToken token) =>
        WriteAsync(assessmentId, async db =>
        {
            var root = await db.PhaseRuns.SingleAsync(x => x.RunId == rootId, token);
            if (root.ParentRunId != null || root.Status is not (ScanStatus.Paused or ScanStatus.Terminated))
                throw new InvalidOperationException("Only a paused or terminated root can resume.");
            var runs = await db.PhaseRuns.Where(x => x.RunId == rootId || x.ParentRunId == rootId).ToListAsync(token);
            foreach (var run in runs.Where(x => x.Status != ScanStatus.Finished))
            {
                run.Status = ScanStatus.Queued;
                run.EndedAtUtc = null;
                if (threads > 0) run.ParametersJson = (PipelineRunSettings.Read(run) with { Threads = threads }).Serialize();
            }
            await UpdateAssessmentAsync(db, assessmentId, ScanStatus.Queued, token);
            return true;
        }, token);

    internal Task<bool> SetPausingAsync(Guid assessmentId, Guid rootId, CancellationToken token) =>
        WriteAsync(assessmentId, async db =>
        {
            var runs = await db.PhaseRuns.Where(x => x.RunId == rootId || x.ParentRunId == rootId).ToListAsync(token);
            foreach (var run in runs.Where(x => x.Status is ScanStatus.Queued or ScanStatus.Running))
                run.Status = ScanStatus.Pausing;
            await UpdateAssessmentAsync(db, assessmentId, ScanStatus.Pausing, token);
            return true;
        }, token);

    internal Task<bool> SettleRootAsync(Guid assessmentId, Guid rootId, ScanStatus status,
        string? error = null, CancellationToken token = default) => WriteAsync(assessmentId, async db =>
    {
        var root = await db.PhaseRuns.SingleAsync(x => x.RunId == rootId, token);
        var children = await db.PhaseRuns.Where(x => x.ParentRunId == rootId).ToListAsync(token);
        if (root.ParentRunId != null || status is not (ScanStatus.Finished or ScanStatus.Paused or ScanStatus.Terminated))
            throw new InvalidOperationException("Invalid pipeline settlement.");
        if (status == ScanStatus.Finished && (children.Any(x => x.Status != ScanStatus.Finished) ||
            root.Kind != PhaseKind.Pipeline && root.Status != ScanStatus.Finished))
            throw new InvalidOperationException("Cannot finish an incomplete phase.");
        foreach (var run in children.Append(root).Where(x => x == root || x.Status != ScanStatus.Finished))
        {
            run.Status = status;
            run.EndedAtUtc = DateTime.UtcNow;
            if (error != null) { run.ErrorCount++; run.LastError = error; }
        }
        db.History.Add(new History
        {
            ScanId = assessmentId, Event = Constants.EventAssessmentStatusChange, EventDate = DateTime.UtcNow,
            Message = $"Pipeline run {rootId}: {status}" + (error == null ? "" : Environment.NewLine + error),
        });
        await UpdateAssessmentAsync(db, assessmentId, status, token);
        return true;
    }, token);

    internal async Task MarkInterruptedRunsAsync(CancellationToken token)
    {
        foreach (var root in await ListRootsAsync(token))
            if (root.Status is ScanStatus.Queued or ScanStatus.Running or ScanStatus.Pausing)
                await SettleRootAsync(root.AssessmentId, root.RunId, ScanStatus.Terminated,
                    "Engine stopped before settlement; resume the original run and checkpoint.", token);
    }

    private static async Task UpdateAssessmentAsync(ScanContext db, Guid id, ScanStatus status, CancellationToken token)
    {
        var scan = await db.Scans.SingleAsync(x => x.ScanId == id, token);
        scan.Status = status;
        scan.EndDate = status is ScanStatus.Finished or ScanStatus.Paused or ScanStatus.Terminated ? DateTime.UtcNow : default;
    }

    private static async Task UpdateParentAsync(ScanContext db, PhaseRunRow phase, CancellationToken token)
    {
        if (phase.ParentRunId is not Guid id) return;
        var parent = await db.PhaseRuns.SingleAsync(x => x.RunId == id, token);
        parent.CompletedRecords = phase.CompletedRecords;
        parent.TotalRecords = phase.TotalRecords;
        parent.ErrorCount = phase.ErrorCount;
        parent.LastError = phase.LastError;
    }

    internal ICollectionJournal CollectionJournal(Guid assessmentId, Guid snapshotId, Guid runId) =>
        new CollectionJournalCapability(this, assessmentId, snapshotId, runId);

    private sealed class CollectionJournalCapability(PipelineStore store, Guid assessmentId, Guid snapshotId, Guid runId) : ICollectionJournal
    {
        public Task CommitAsync(CollectionRecord record, CancellationToken cancellationToken) =>
            store.CommitObservationAsync(assessmentId, snapshotId, runId, record, cancellationToken);

        public async Task<IReadOnlyList<CollectedObservation>> ReadCommittedAsync(CancellationToken cancellationToken, bool metadataOnly = false)
        {
            using var db = store.CreateContext(assessmentId, readOnly: true);
            var query = db.SourceObservations.AsNoTracking().Where(x => x.SnapshotId == snapshotId);
            if (metadataOnly) query = query.Select(x => new SourceObservationRow
            {
                ObservationId = x.ObservationId, SourceIdentity = x.SourceIdentity, SourceRevision = x.SourceRevision,
                AcquisitionStatus = x.AcquisitionStatus, AcquisitionError = x.AcquisitionError, MetadataJson = x.MetadataJson,
            });
            var rows = await query.OrderBy(x => x.SourceIdentity).ToListAsync(cancellationToken);
            return rows.Select(row =>
            {
                if (!metadataOnly) SnapshotManifest.ValidateContent(row);
                return Observation(row);
            }).ToArray();
        }

        public async Task<CollectedObservation> ReadAsync(Guid observationId, CancellationToken cancellationToken)
        {
            using var db = store.CreateContext(assessmentId, readOnly: true);
            var row = await db.SourceObservations.AsNoTracking()
                .SingleAsync(x => x.SnapshotId == snapshotId && x.ObservationId == observationId, cancellationToken);
            SnapshotManifest.ValidateContent(row);
            return Observation(row);
        }

        private static CollectedObservation Observation(SourceObservationRow row) =>
            new(row.ObservationId, row.SourceIdentity, row.SourceRevision, row.AcquisitionStatus,
                new VersionedJson(row.MetadataJson), row.RawBytes?.ToArray(), row.AcquisitionError);
    }
}
