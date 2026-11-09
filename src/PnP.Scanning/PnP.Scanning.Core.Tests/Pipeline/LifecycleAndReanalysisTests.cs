#nullable enable
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Pipeline.Collection.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Pipeline.Orchestration;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Storage.Pipeline;
using Xunit;

namespace PnP.Scanning.Core.Tests.Pipeline;

/// <summary>
/// Verifies cancellation, checkpoint restart, pinned rules, and independent offline analysis runs.
/// </summary>
[Trait("Category", "Pipeline")]
public sealed class LifecycleAndReanalysisTests
{
    /// <summary>
    /// Verifies that legacy admission and pipeline enqueue share an atomic capacity gate.
    /// </summary>
    [Fact]
    public async Task Legacy_admission_and_pipeline_enqueue_share_an_atomic_capacity_gate()
    {
        using StoreCase? data = new StoreCase("pipeline-shared-admission");
        FixtureControl? control = new FixtureControl { BlockCollectionAt = FixedSource.Read()[0].ObservationId };
        PipelineCoordinator? coordinator = new PipelineCoordinator(data.Store, FixedSource.Registry(control), new ForbiddenOnlineEnvironment());
        TaskCompletionSource? entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource? release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>? legacy = coordinator.AdmitLegacyAsync(async () =>
        {
            entered.SetResult();
            await release.Task;
            return true;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<PhaseReply>? queued = coordinator.CollectAsync(FixedSource.Request());
        Assert.False(queued.IsCompleted);
        release.SetResult();
        Assert.True(await legacy);
        PhaseReply? ticket = await queued;
        await coordinator.PauseAsync(Guid.Parse(ticket.AssessmentId), false);
        Assert.Equal(ScanStatus.Paused, await coordinator.WaitForCompletionAsync(Guid.Parse(ticket.AssessmentId)));
    }

    /// <summary>
    /// Verifies that queued pipeline capacity and pause all share existing lifecycle limits.
    /// </summary>
    [Fact]
    public async Task Queued_pipeline_capacity_and_pause_all_share_existing_lifecycle_limits()
    {
        using StoreCase? data = new StoreCase("pipeline-capacity");
        FixtureControl? control = new FixtureControl { BlockCollectionAt = FixedSource.Read()[0].ObservationId };
        PipelineCoordinator? coordinator = new PipelineCoordinator(data.Store, FixedSource.Registry(control), new ForbiddenOnlineEnvironment());
        List<PhaseReply>? tickets = new List<PhaseReply>();
        for (int index = 0; index < ScanManager.MaxParallelScans; index++) tickets.Add(await coordinator.CollectAsync(FixedSource.Request()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CollectAsync(FixedSource.Request()));
        Assert.Equal(3, data.Store.AssessmentsOnDisk().Count());
        await coordinator.PauseAsync(Guid.Empty, true);
        Assert.Equal(0, coordinator.ActiveCount);
        foreach (PhaseReply? ticket in tickets)
        {
            PhaseRunRow? root = await data.Store.LatestRootAsync(Guid.Parse(ticket.AssessmentId));
            Assert.Equal(ScanStatus.Paused, root!.Status);
            Assert.Equal(0, root.CompletedRecords);
        }
    }
    /// <summary>
    /// Verifies that infrastructure collection failure does not seal or start analysis and can resume original checkpoint.
    /// </summary>
    [Fact]
    public async Task Infrastructure_collection_failure_does_not_seal_or_start_analysis_and_can_resume_original_checkpoint()
    {
        using StoreCase? data = new StoreCase("pipeline-infrastructure");
        ForbiddenOnlineEnvironment? environment = new ForbiddenOnlineEnvironment();
        FixtureControl? control = new FixtureControl();
        ModuleRegistry? registration = new ModuleRegistry(
            [new(FixedSource.ModuleKey, FixedSource.InputVersion, () => new InterruptedCollector())],
            [new(FixedSource.ModuleKey, FixedSource.Rule1, [FixedSource.InputVersion], () => control.CreateAnalyzer(FixedSource.Rule1))],
            new Dictionary<string, string> { [FixedSource.ModuleKey] = FixedSource.Rule1 });
        PipelineCoordinator? failed = new PipelineCoordinator(data.Store, registration, environment);
        PhaseReply? ticket = await failed.StartPipelineAsync(new() { Collection = FixedSource.Request() });
        Guid id = Guid.Parse(ticket.AssessmentId);
        Assert.Equal(ScanStatus.Terminated, await failed.WaitForCompletionAsync(id));
        using (ScanContext? db = data.Store.CreateContext(id))
        {
            Assert.False((await db.SourceSnapshots.SingleAsync()).IsSealed);
            Assert.Equal(1, await db.SourceObservations.CountAsync());
            Assert.Equal(0, await db.AnalysisRuns.CountAsync());
            Assert.Equal(0, await db.AnalysisResults.CountAsync());
            Assert.Equal(ScanStatus.Terminated, (await db.Scans.SingleAsync()).Status);
            PhaseRunRow? phase = await db.PhaseRuns.SingleAsync(x => x.Kind == PhaseKind.Collection);
            Assert.Equal(1, phase.CompletedRecords);
            Assert.Equal(1, new VersionedJson(phase.CheckpointJson!).Value.GetProperty("nextIndex").GetInt32());
        }
        Assert.Equal(0, control.AnalysisFactories);
        PipelineCoordinator? resumed = new PipelineCoordinator(new PipelineStore(data.DirectoryPath), FixedSource.Registry(control), environment);
        PhaseReply? restarted = await resumed.RestartAsync(id);
        Assert.Equal(ticket.RunId, restarted.RunId);
        Assert.Equal(ticket.AnalysisRunId, restarted.AnalysisRunId);
        Assert.Equal(ScanStatus.Finished, await resumed.WaitForCompletionAsync(id));
        using ScanContext? read = data.Store.CreateContext(id);
        Assert.Equal(6, await read.SourceObservations.CountAsync());
        Assert.Equal(6, await read.AnalysisResults.CountAsync());
        Assert.True(await read.History.AnyAsync(x => x.Message.Contains("unsettled infrastructure")));
    }

    /// <summary>
    /// Emits one recorded receipt before a controlled infrastructure failure.
    /// </summary>
    private sealed class InterruptedCollector : ICollectionModule
    {
        public string ModuleKey => FixedSource.ModuleKey;
        public string InputVersion => FixedSource.InputVersion;
        /// <summary>
        /// Emits the recorded receipts required by the controlled failure or incompleteness test.
        /// </summary>
        public async IAsyncEnumerable<CollectionRecord> CollectAsync(CollectionContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new(FixedSource.Read()[0], VersionedJson.From(new { nextIndex = 1 }), 6);
            await Task.Yield();
            throw new IOException("fixture unsettled infrastructure failure");
        }
    }

    /// <summary>
    /// Verifies that seal failure does not start analysis or force finished status.
    /// </summary>
    [Fact]
    public async Task Seal_failure_does_not_start_analysis_or_force_finished_status()
    {
        using StoreCase? data = new StoreCase("pipeline-seal-failure");
        FixtureControl? control = new FixtureControl();
        ModuleRegistry? registration = new ModuleRegistry(
            [new(FixedSource.ModuleKey, FixedSource.InputVersion, () => new IncompleteManifestCollector())],
            [new(FixedSource.ModuleKey, FixedSource.Rule1, [FixedSource.InputVersion], () => control.CreateAnalyzer(FixedSource.Rule1))],
            new Dictionary<string, string> { [FixedSource.ModuleKey] = FixedSource.Rule1 });
        PipelineCoordinator? coordinator = new PipelineCoordinator(data.Store, registration, new ForbiddenOnlineEnvironment());
        PhaseReply? ticket = await coordinator.StartPipelineAsync(new() { Collection = FixedSource.Request() });
        Guid id = Guid.Parse(ticket.AssessmentId);
        Assert.Equal(ScanStatus.Terminated, await coordinator.WaitForCompletionAsync(id));
        using ScanContext? db = data.Store.CreateContext(id);
        Assert.False((await db.SourceSnapshots.SingleAsync()).IsSealed);
        Assert.Equal(0, await db.AnalysisRuns.CountAsync());
        Assert.Equal(ScanStatus.Terminated, (await db.Scans.SingleAsync()).Status);
        Assert.Equal(0, control.AnalysisFactories);
    }

    /// <summary>
    /// Emits fewer recorded members than its declared expected count.
    /// </summary>
    private sealed class IncompleteManifestCollector : ICollectionModule
    {
        public string ModuleKey => FixedSource.ModuleKey;
        public string InputVersion => FixedSource.InputVersion;
        /// <summary>
        /// Emits the recorded receipts required by the controlled failure or incompleteness test.
        /// </summary>
        public async IAsyncEnumerable<CollectionRecord> CollectAsync(CollectionContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (CollectedObservation? row in FixedSource.Read())
            {
                await Task.Yield();
                yield return new(row, VersionedJson.Empty, 7);
            }
        }
    }

    /// <summary>
    /// Verifies that collection pause and restart use saved record checkpoint without legacy cleanup.
    /// </summary>
    [Fact]
    public async Task Collection_pause_and_restart_use_saved_record_checkpoint_without_legacy_cleanup()
    {
        using StoreCase? data = new StoreCase("pipeline-collection-resume");
        FixtureControl? control = new FixtureControl { BlockCollectionAt = FixedSource.Read()[1].ObservationId };
        ForbiddenOnlineEnvironment? environment = new ForbiddenOnlineEnvironment();
        PipelineCoordinator? coordinator = new PipelineCoordinator(data.Store, FixedSource.Registry(control), environment);
        PhaseReply? ticket = await coordinator.CollectAsync(FixedSource.Request());
        Guid id = Guid.Parse(ticket.AssessmentId);
        await control.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await coordinator.PauseAsync(id, false);
        using (ScanContext? db = data.Store.CreateContext(id))
        {
            PhaseRunRow? phase = await db.PhaseRuns.SingleAsync();
            Assert.Equal(ScanStatus.Paused, phase.Status);
            Assert.Equal(1, phase.CompletedRecords);
            Assert.Equal(1, new VersionedJson(phase.CheckpointJson!).Value.GetProperty("nextIndex").GetInt32());
            Assert.False((await db.SourceSnapshots.SingleAsync()).IsSealed);
            Assert.Equal(1, await db.SourceObservations.CountAsync());
        }
        control.Release();
        await coordinator.RestartAsync(id);
        Assert.Equal(ScanStatus.Finished, await coordinator.WaitForCompletionAsync(id));
        Assert.Equal(6, (await data.Store.OpenSnapshotAsync(id, Guid.Parse(ticket.SnapshotId))).ObservationIds.Count);
        Assert.Equal(2, control.CollectionStarts);
        Assert.Equal(0, environment.OnlineRequests);
    }

    /// <summary>
    /// Verifies that combined restart after seal requires only the pinned analyzer.
    /// </summary>
    [Fact]
    public async Task Combined_restart_after_seal_requires_only_the_pinned_analyzer()
    {
        using StoreCase? data = new StoreCase("pipeline-after-seal");
        FixtureControl? control = new FixtureControl { FailAnalysisFactoryOnce = true };
        ForbiddenOnlineEnvironment? environment = new ForbiddenOnlineEnvironment();
        PipelineCoordinator? coordinator = new PipelineCoordinator(data.Store, FixedSource.Registry(control), environment);
        PhaseReply? ticket = await coordinator.StartPipelineAsync(new() { Collection = FixedSource.Request() });
        Guid id = Guid.Parse(ticket.AssessmentId);
        Assert.Equal(ScanStatus.Terminated, await coordinator.WaitForCompletionAsync(id));
        SnapshotInfo? snapshot = await data.Store.OpenSnapshotAsync(id, Guid.Parse(ticket.SnapshotId));
        using (ScanContext? db = data.Store.CreateContext(id))
        {
            Assert.Equal(ScanStatus.Finished, (await db.PhaseRuns.SingleAsync(x => x.Kind == PhaseKind.Collection)).Status);
            Assert.Equal(PhaseKind.Analysis, (await db.PhaseRuns.SingleAsync(x => x.ParentRunId == null)).CurrentPhase);
            Assert.Equal(0, await db.AnalysisRuns.CountAsync());
        }
        environment.ForbidRestore = true;
        PipelineStore? reopened = new PipelineStore(data.DirectoryPath);
        PipelineCoordinator? resumed = new PipelineCoordinator(reopened, FixedSource.Registry(control, collector: false, current: FixedSource.Rule2), environment);
        PhaseReply? reply = await resumed.RestartAsync(id);
        Assert.Equal(ticket.AnalysisRunId, reply.AnalysisRunId);
        Assert.Equal(FixedSource.Rule1, reply.RuleVersion);
        Assert.Equal(ScanStatus.Finished, await resumed.WaitForCompletionAsync(id));
        Assert.Equal(snapshot.ManifestDigest, (await reopened.OpenSnapshotAsync(id, snapshot.SnapshotId)).ManifestDigest);
        Assert.Equal(1, control.CollectionStarts);
        Assert.Equal(1, environment.RestoreRequests);
        Assert.Equal(0, environment.OnlineRequests);
    }

    /// <summary>
    /// Verifies that out of order parallel results resume by committed membership instead of last record.
    /// </summary>
    [Fact]
    public async Task Out_of_order_parallel_results_resume_by_committed_membership_instead_of_last_record()
    {
        using StoreCase? data = new StoreCase("pipeline-parallel-resume");
        FixtureControl? control = new FixtureControl();
        ForbiddenOnlineEnvironment? environment = new ForbiddenOnlineEnvironment();
        PipelineCoordinator? coordinator = new PipelineCoordinator(data.Store, FixedSource.Registry(control), environment);
        PhaseReply? collection = await coordinator.CollectAsync(FixedSource.Request(3));
        Guid id = Guid.Parse(collection.AssessmentId);
        await coordinator.WaitForCompletionAsync(id);
        environment.ForbidRestore = true;
        control.BlockAnalysisAt = FixedSource.Read()[0].ObservationId;
        PhaseReply? analysis = await coordinator.AnalyzeAsync(new() { Id = collection.AssessmentId, SnapshotId = collection.SnapshotId, Threads = 3 });
        Guid analysisId = Guid.Parse(analysis.AnalysisRunId);
        await control.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await StoreCase.WaitUntilAsync(async () => await data.ResultCountAsync(id, analysisId) == 5);
        await coordinator.PauseAsync(id, false);
        Assert.Equal(5, await data.ResultCountAsync(id, analysisId));
        control.Release();
        await coordinator.RestartAsync(id);
        Assert.Equal(ScanStatus.Finished, await coordinator.WaitForCompletionAsync(id));
        Assert.Equal(6, await data.ResultCountAsync(id, analysisId));
        foreach (CollectedObservation? row in FixedSource.Read().Skip(1)) Assert.Equal(1, control.AnalysisCalls[(FixedSource.Rule1, row.ObservationId)]);
        Assert.Equal(2, control.AnalysisCalls[(FixedSource.Rule1, FixedSource.Read()[0].ObservationId)]);
        Assert.Equal(1, control.CollectionStarts);
    }

    /// <summary>
    /// Verifies that corruption during analysis terminates the run and preserves committed results and diagnostics.
    /// </summary>
    [Fact]
    public async Task Corruption_during_analysis_terminates_the_run_and_preserves_committed_results_and_diagnostics()
    {
        using StoreCase? data = new StoreCase("pipeline-analysis-corruption");
        FixtureControl? control = new FixtureControl();
        ForbiddenOnlineEnvironment? environment = new ForbiddenOnlineEnvironment();
        PipelineCoordinator? coordinator = new PipelineCoordinator(data.Store, FixedSource.Registry(control), environment);
        PhaseReply? collection = await coordinator.CollectAsync(FixedSource.Request());
        Guid id = Guid.Parse(collection.AssessmentId);
        await coordinator.WaitForCompletionAsync(id);
        control.BlockAnalysisAt = FixedSource.Read()[1].ObservationId;
        PhaseReply? analysis = await coordinator.AnalyzeAsync(new() { Id = collection.AssessmentId, SnapshotId = collection.SnapshotId });
        await control.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
        using (ScanContext? db = data.Store.CreateContext(id))
        {
            Guid observation = FixedSource.Read()[1].ObservationId;
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE SourceObservations SET Length = Length + 1 WHERE ObservationId = {observation}");
        }
        control.Release();
        Assert.Equal(ScanStatus.Terminated, await coordinator.WaitForCompletionAsync(id));
        Assert.Equal(1, await data.ResultCountAsync(id, Guid.Parse(analysis.AnalysisRunId)));
        using ScanContext? read = data.Store.CreateContext(id);
        PhaseRunRow? root = await read.PhaseRuns.SingleAsync(x => x.RunId == Guid.Parse(analysis.RunId));
        Assert.Contains("SHA-256 mismatch", root.LastError!, StringComparison.Ordinal);
        Assert.Equal(1, root.CompletedRecords);
        Assert.Equal(ScanStatus.Terminated, (await read.Scans.SingleAsync()).Status);
    }

    /// <summary>
    /// Verifies that repeated rules and later sources create independent results without replacing selected input.
    /// </summary>
    [Fact]
    public async Task Repeated_rules_and_later_sources_create_independent_results_without_replacing_selected_input()
    {
        using StoreCase? data = new StoreCase("pipeline-reanalysis");
        (Guid Assessment, Guid Snapshot, Guid Run, ISnapshotWriter Writer) seed = await data.UnsealedAsync();
        SnapshotInfo? original = await seed.Writer.SealAsync(default);
        FixtureControl? control = new FixtureControl();
        ForbiddenOnlineEnvironment? environment = new ForbiddenOnlineEnvironment { ForbidRestore = true };
        PipelineCoordinator? coordinator = new PipelineCoordinator(data.Store, FixedSource.Registry(control, collector: false), environment);
        VersionedJson? parameters = VersionedJson.From(new { configuration = "fixed-S1" });
        List<PhaseReply>? runs = new List<PhaseReply>();
        for (int index = 0; index < 2; index++)
        {
            runs.Add(await coordinator.AnalyzeAsync(new()
            {
                Id = seed.Assessment.ToString(), SnapshotId = seed.Snapshot.ToString(), RuleVersion = FixedSource.Rule1,
                ParametersJson = parameters.Json,
            }));
            Assert.Equal(ScanStatus.Finished, await coordinator.WaitForCompletionAsync(seed.Assessment));
        }
        CollectedObservation[]? later = FixedSource.Read().Select((x, index) => x with
        {
            ObservationId = Guid.Parse($"10000000-0000-0000-0000-{index + 1:000000000000}"),
            SourceRevision = "later-revision", RawBytes = x.RawBytes == null ? null : new byte[] { 9, 9, 9 },
        }).ToArray();
        (Guid Assessment, Guid Snapshot, Guid Run, ISnapshotWriter Writer) second = await data.UnsealedAsync(later, seed.Assessment);
        await second.Writer.SealAsync(default);
        PipelineCoordinator? newCoordinator = new PipelineCoordinator(new PipelineStore(data.DirectoryPath),
            FixedSource.Registry(control, collector: false, current: FixedSource.Rule2), environment);
        runs.Add(await newCoordinator.AnalyzeAsync(new()
        {
            Id = seed.Assessment.ToString(), SnapshotId = seed.Snapshot.ToString(), ParametersJson = parameters.Json,
        }));
        Assert.Equal(ScanStatus.Finished, await newCoordinator.WaitForCompletionAsync(seed.Assessment));
        Assert.Equal(3, runs.Select(x => x.AnalysisRunId).Distinct().Count());
        using ScanContext? db = data.Store.CreateContext(seed.Assessment);
        List<AnalysisRunRow>? analysisRuns = await db.AnalysisRuns.ToListAsync();
        Assert.Equal(2, analysisRuns.Count(x => x.RuleVersion == FixedSource.Rule1));
        Assert.Equal(1, analysisRuns.Count(x => x.RuleVersion == FixedSource.Rule2));
        Assert.All(analysisRuns, x =>
        {
            Assert.Equal(seed.Snapshot, x.SnapshotId);
            Assert.Equal(parameters.Json, x.ParametersJson);
            Assert.Equal(original.ManifestDigest, x.ManifestDigest);
        });
        List<AnalysisResultRow>? results = await db.AnalysisResults.ToListAsync();
        Assert.Equal(18, results.Count);
        Assert.DoesNotContain(results, x => later.Any(y => y.ObservationId == x.ObservationId));
        foreach (CollectedObservation? expected in FixedSource.Read())
        {
            SourceRecord? source = await data.Store.SnapshotReader(seed.Assessment, seed.Snapshot).ReadAsync(expected.ObservationId, default);
            Assert.Equal(expected.RawBytes, source.Artifact.GetBytes());
            Assert.Equal(expected.SourceRevision, source.SourceRevision);
            foreach (AnalysisResultRow? result in results.Where(x => x.ObservationId == expected.ObservationId))
            {
                System.Text.Json.JsonElement payload = new VersionedJson(result.PayloadJson).Value;
                Assert.Equal(expected.SourceRevision, payload.GetProperty("SourceRevision").GetString());
                Assert.Equal("fixed-S1", payload.GetProperty("parameters").GetProperty("configuration").GetString());
            }
        }
        Assert.Equal(original.ManifestDigest, (await data.Store.OpenSnapshotAsync(seed.Assessment, seed.Snapshot)).ManifestDigest);
        Assert.Equal(0, environment.OnlineRequests);
        Assert.Equal(0, environment.RestoreRequests);
        AnalysisRunRow? firstRun = analysisRuns[0];
        AnalysisResultRow? originalResult = results.First(x => x.AnalysisRunId == firstRun.AnalysisRunId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => data.Store.ResultWriter(seed.Assessment, firstRun.AnalysisRunId)
            .WriteAsync(new(originalResult.ObservationId, originalResult.Outcome, "replacement",
                new VersionedJson(originalResult.PayloadJson)), default));
        await db.Database.ExecuteSqlRawAsync("UPDATE AnalysisRuns SET RuleVersion = 'replacement'");
        await Assert.ThrowsAsync<SnapshotIntegrityException>(() =>
            data.Store.ResultWriter(seed.Assessment, firstRun.AnalysisRunId).GetCompletedObservationIdsAsync(default));
    }
}
