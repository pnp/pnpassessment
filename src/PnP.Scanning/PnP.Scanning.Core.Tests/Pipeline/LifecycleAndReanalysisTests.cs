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

[Trait("Category", "Pipeline")]
public sealed class LifecycleAndReanalysisTests
{
    [Fact]
    public async Task Queued_pipeline_capacity_and_pause_all_share_existing_lifecycle_limits()
    {
        using var data = new StoreCase("pipeline-capacity");
        var control = new FixtureControl { BlockCollectionAt = FixedSource.Read()[0].ObservationId };
        var coordinator = new PipelineCoordinator(data.Store, FixedSource.Registry(control), new ForbiddenOnlineEnvironment());
        var tickets = new List<PhaseReply>();
        for (int index = 0; index < ScanManager.MaxParallelScans; index++) tickets.Add(await coordinator.CollectAsync(FixedSource.Request()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CollectAsync(FixedSource.Request()));
        Assert.Equal(3, data.Store.AssessmentsOnDisk().Count());
        await coordinator.PauseAsync(Guid.Empty, true);
        Assert.Equal(0, coordinator.ActiveCount);
        foreach (var ticket in tickets)
        {
            var root = await data.Store.LatestRootAsync(Guid.Parse(ticket.AssessmentId));
            Assert.Equal(ScanStatus.Paused, root!.Status);
            Assert.Equal(0, root.CompletedRecords);
        }
    }
    [Fact]
    public async Task Infrastructure_collection_failure_does_not_seal_or_start_analysis_and_can_resume_original_checkpoint()
    {
        using var data = new StoreCase("pipeline-infrastructure");
        var environment = new ForbiddenOnlineEnvironment();
        var control = new FixtureControl();
        var registration = new ModuleRegistry(
            [new(FixedSource.ModuleKey, FixedSource.InputVersion, () => new InterruptedCollector())],
            [new(FixedSource.ModuleKey, FixedSource.Rule1, [FixedSource.InputVersion], () => control.CreateAnalyzer(FixedSource.Rule1))],
            new Dictionary<string, string> { [FixedSource.ModuleKey] = FixedSource.Rule1 });
        var failed = new PipelineCoordinator(data.Store, registration, environment);
        var ticket = await failed.StartPipelineAsync(new() { Collection = FixedSource.Request() });
        var id = Guid.Parse(ticket.AssessmentId);
        Assert.Equal(ScanStatus.Terminated, await failed.WaitForCompletionAsync(id));
        using (var db = data.Store.CreateContext(id))
        {
            Assert.False((await db.SourceSnapshots.SingleAsync()).IsSealed);
            Assert.Equal(1, await db.SourceObservations.CountAsync());
            Assert.Equal(1, await db.SourceArtifacts.CountAsync());
            Assert.Equal(0, await db.AnalysisRuns.CountAsync());
            Assert.Equal(0, await db.AnalysisResults.CountAsync());
            Assert.Equal(ScanStatus.Terminated, (await db.Scans.SingleAsync()).Status);
            var phase = await db.PhaseRuns.SingleAsync(x => x.Kind == PhaseKind.Collection);
            Assert.Equal(1, phase.CompletedRecords);
            Assert.Equal(1, new VersionedJson(phase.CheckpointJson!).Value.GetProperty("nextIndex").GetInt32());
        }
        Assert.Equal(0, control.AnalysisFactories);
        var resumed = new PipelineCoordinator(new PipelineStore(data.DirectoryPath), FixedSource.Registry(control), environment);
        var restarted = await resumed.RestartAsync(id);
        Assert.Equal(ticket.RunId, restarted.RunId);
        Assert.Equal(ticket.AnalysisRunId, restarted.AnalysisRunId);
        Assert.Equal(ScanStatus.Finished, await resumed.WaitForCompletionAsync(id));
        using var read = data.Store.CreateContext(id);
        Assert.Equal(6, await read.SourceObservations.CountAsync());
        Assert.Equal(6, await read.AnalysisResults.CountAsync());
        Assert.Contains("unsettled infrastructure", (await read.PhaseRuns.SingleAsync(x => x.ParentRunId == null)).ErrorsJson, StringComparison.Ordinal);
    }

    private sealed class InterruptedCollector : ICollectionModule
    {
        public string ModuleKey => FixedSource.ModuleKey;
        public string InputVersion => FixedSource.InputVersion;
        public async IAsyncEnumerable<CollectionRecord> CollectAsync(CollectionContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new(FixedSource.Read()[0], VersionedJson.From(new { nextIndex = 1 }), 6);
            await Task.Yield();
            throw new IOException("fixture unsettled infrastructure failure");
        }
    }

    [Fact]
    public async Task Seal_failure_does_not_start_analysis_or_force_finished_status()
    {
        using var data = new StoreCase("pipeline-seal-failure");
        var control = new FixtureControl();
        var registration = new ModuleRegistry(
            [new(FixedSource.ModuleKey, FixedSource.InputVersion, () => new IncompleteManifestCollector())],
            [new(FixedSource.ModuleKey, FixedSource.Rule1, [FixedSource.InputVersion], () => control.CreateAnalyzer(FixedSource.Rule1))],
            new Dictionary<string, string> { [FixedSource.ModuleKey] = FixedSource.Rule1 });
        var coordinator = new PipelineCoordinator(data.Store, registration, new ForbiddenOnlineEnvironment());
        var ticket = await coordinator.StartPipelineAsync(new() { Collection = FixedSource.Request() });
        var id = Guid.Parse(ticket.AssessmentId);
        Assert.Equal(ScanStatus.Terminated, await coordinator.WaitForCompletionAsync(id));
        using var db = data.Store.CreateContext(id);
        Assert.False((await db.SourceSnapshots.SingleAsync()).IsSealed);
        Assert.Equal(0, await db.AnalysisRuns.CountAsync());
        Assert.Equal(ScanStatus.Terminated, (await db.Scans.SingleAsync()).Status);
        Assert.Equal(0, control.AnalysisFactories);
    }

    private sealed class IncompleteManifestCollector : ICollectionModule
    {
        public string ModuleKey => FixedSource.ModuleKey;
        public string InputVersion => FixedSource.InputVersion;
        public async IAsyncEnumerable<CollectionRecord> CollectAsync(CollectionContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var row in FixedSource.Read())
            {
                await Task.Yield();
                yield return new(row, VersionedJson.Empty, 7);
            }
        }
    }

    [Fact]
    public async Task Collection_pause_and_restart_use_saved_record_checkpoint_without_legacy_cleanup()
    {
        using var data = new StoreCase("pipeline-collection-resume");
        var control = new FixtureControl { BlockCollectionAt = FixedSource.Read()[1].ObservationId };
        var environment = new ForbiddenOnlineEnvironment();
        var coordinator = new PipelineCoordinator(data.Store, FixedSource.Registry(control), environment);
        var ticket = await coordinator.CollectAsync(FixedSource.Request());
        var id = Guid.Parse(ticket.AssessmentId);
        await control.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await coordinator.PauseAsync(id, false);
        using (var db = data.Store.CreateContext(id))
        {
            var phase = await db.PhaseRuns.SingleAsync();
            Assert.Equal(ScanStatus.Paused, phase.Status);
            Assert.Equal(1, phase.CompletedRecords);
            Assert.Equal(1, new VersionedJson(phase.CheckpointJson!).Value.GetProperty("nextIndex").GetInt32());
            Assert.False((await db.SourceSnapshots.SingleAsync()).IsSealed);
            Assert.Equal(1, await db.SourceArtifacts.CountAsync());
        }
        control.Release();
        await coordinator.RestartAsync(id);
        Assert.Equal(ScanStatus.Finished, await coordinator.WaitForCompletionAsync(id));
        Assert.Equal(6, (await data.Store.OpenSnapshotAsync(id, Guid.Parse(ticket.SnapshotId))).ObservationIds.Count);
        Assert.Equal(2, control.CollectionStarts);
        Assert.Equal(0, environment.OnlineRequests);
    }

    [Fact]
    public async Task Combined_restart_after_seal_requires_only_the_pinned_analyzer()
    {
        using var data = new StoreCase("pipeline-after-seal");
        var control = new FixtureControl { FailAnalysisFactoryOnce = true };
        var environment = new ForbiddenOnlineEnvironment();
        var coordinator = new PipelineCoordinator(data.Store, FixedSource.Registry(control), environment);
        var ticket = await coordinator.StartPipelineAsync(new() { Collection = FixedSource.Request() });
        var id = Guid.Parse(ticket.AssessmentId);
        Assert.Equal(ScanStatus.Terminated, await coordinator.WaitForCompletionAsync(id));
        var snapshot = await data.Store.OpenSnapshotAsync(id, Guid.Parse(ticket.SnapshotId));
        using (var db = data.Store.CreateContext(id))
        {
            Assert.Equal(ScanStatus.Finished, (await db.PhaseRuns.SingleAsync(x => x.Kind == PhaseKind.Collection)).Status);
            Assert.Equal(PhaseKind.Analysis, (await db.PhaseRuns.SingleAsync(x => x.ParentRunId == null)).CurrentPhase);
            Assert.Equal(0, await db.AnalysisRuns.CountAsync());
        }
        environment.ForbidRestore = true;
        var reopened = new PipelineStore(data.DirectoryPath);
        var resumed = new PipelineCoordinator(reopened, FixedSource.Registry(control, collector: false, current: FixedSource.Rule2), environment);
        var reply = await resumed.RestartAsync(id);
        Assert.Equal(ticket.AnalysisRunId, reply.AnalysisRunId);
        Assert.Equal(FixedSource.Rule1, reply.RuleVersion);
        Assert.Equal(ScanStatus.Finished, await resumed.WaitForCompletionAsync(id));
        Assert.Equal(snapshot.ManifestDigest, (await reopened.OpenSnapshotAsync(id, snapshot.SnapshotId)).ManifestDigest);
        Assert.Equal(1, control.CollectionStarts);
        Assert.Equal(1, environment.RestoreRequests);
        Assert.Equal(0, environment.OnlineRequests);
    }

    [Fact]
    public async Task Out_of_order_parallel_results_resume_by_committed_membership_instead_of_last_record()
    {
        using var data = new StoreCase("pipeline-parallel-resume");
        var control = new FixtureControl();
        var environment = new ForbiddenOnlineEnvironment();
        var coordinator = new PipelineCoordinator(data.Store, FixedSource.Registry(control), environment);
        var collection = await coordinator.CollectAsync(FixedSource.Request(3));
        var id = Guid.Parse(collection.AssessmentId);
        await coordinator.WaitForCompletionAsync(id);
        environment.ForbidRestore = true;
        control.BlockAnalysisAt = FixedSource.Read()[0].ObservationId;
        var analysis = await coordinator.AnalyzeAsync(new() { Id = collection.AssessmentId, SnapshotId = collection.SnapshotId, Threads = 3 });
        var analysisId = Guid.Parse(analysis.AnalysisRunId);
        await control.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await StoreCase.WaitUntilAsync(async () => await data.ResultCountAsync(id, analysisId) == 5);
        await coordinator.PauseAsync(id, false);
        Assert.Equal(5, await data.ResultCountAsync(id, analysisId));
        control.Release();
        await coordinator.RestartAsync(id);
        Assert.Equal(ScanStatus.Finished, await coordinator.WaitForCompletionAsync(id));
        Assert.Equal(6, await data.ResultCountAsync(id, analysisId));
        foreach (var row in FixedSource.Read().Skip(1)) Assert.Equal(1, control.AnalysisCalls[(FixedSource.Rule1, row.ObservationId)]);
        Assert.Equal(2, control.AnalysisCalls[(FixedSource.Rule1, FixedSource.Read()[0].ObservationId)]);
        Assert.Equal(1, control.CollectionStarts);
    }

    [Fact]
    public async Task Corruption_during_analysis_terminates_the_run_and_preserves_committed_results_and_diagnostics()
    {
        using var data = new StoreCase("pipeline-analysis-corruption");
        var control = new FixtureControl();
        var environment = new ForbiddenOnlineEnvironment();
        var coordinator = new PipelineCoordinator(data.Store, FixedSource.Registry(control), environment);
        var collection = await coordinator.CollectAsync(FixedSource.Request());
        var id = Guid.Parse(collection.AssessmentId);
        await coordinator.WaitForCompletionAsync(id);
        control.BlockAnalysisAt = FixedSource.Read()[1].ObservationId;
        var analysis = await coordinator.AnalyzeAsync(new() { Id = collection.AssessmentId, SnapshotId = collection.SnapshotId });
        await control.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
        using (var db = data.Store.CreateContext(id))
        {
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER Pipeline_SourceArtifacts_sealed_UPDATE");
            var observation = FixedSource.Read()[1].ObservationId;
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE SourceArtifacts SET Length = Length + 1 WHERE ObservationId = {observation}");
        }
        control.Release();
        Assert.Equal(ScanStatus.Terminated, await coordinator.WaitForCompletionAsync(id));
        Assert.Equal(1, await data.ResultCountAsync(id, Guid.Parse(analysis.AnalysisRunId)));
        using var read = data.Store.CreateContext(id);
        var root = await read.PhaseRuns.SingleAsync(x => x.RunId == Guid.Parse(analysis.RunId));
        Assert.Contains("SHA-256 mismatch", root.LastError!, StringComparison.Ordinal);
        Assert.Equal(1, root.CompletedRecords);
        Assert.Equal(ScanStatus.Terminated, (await read.Scans.SingleAsync()).Status);
    }

    [Fact]
    public async Task Repeated_rules_and_later_sources_create_independent_results_without_replacing_selected_input()
    {
        using var data = new StoreCase("pipeline-reanalysis");
        var seed = await data.UnsealedAsync();
        var original = await seed.Writer.SealAsync(default);
        var control = new FixtureControl();
        var environment = new ForbiddenOnlineEnvironment { ForbidRestore = true };
        var coordinator = new PipelineCoordinator(data.Store, FixedSource.Registry(control, collector: false), environment);
        var parameters = VersionedJson.From(new { configuration = "fixed-S1" });
        var runs = new List<PhaseReply>();
        for (var index = 0; index < 2; index++)
        {
            runs.Add(await coordinator.AnalyzeAsync(new()
            {
                Id = seed.Assessment.ToString(), SnapshotId = seed.Snapshot.ToString(), RuleVersion = FixedSource.Rule1,
                ParametersJson = parameters.Json,
            }));
            Assert.Equal(ScanStatus.Finished, await coordinator.WaitForCompletionAsync(seed.Assessment));
        }
        var later = FixedSource.Read().Select((x, index) => x with
        {
            ObservationId = Guid.Parse($"10000000-0000-0000-0000-{index + 1:000000000000}"),
            SourceRevision = "later-revision", RawBytes = x.RawBytes == null ? null : new byte[] { 9, 9, 9 },
        }).ToArray();
        var second = await data.UnsealedAsync(later, seed.Assessment);
        await second.Writer.SealAsync(default);
        var newCoordinator = new PipelineCoordinator(new PipelineStore(data.DirectoryPath),
            FixedSource.Registry(control, collector: false, current: FixedSource.Rule2), environment);
        runs.Add(await newCoordinator.AnalyzeAsync(new()
        {
            Id = seed.Assessment.ToString(), SnapshotId = seed.Snapshot.ToString(), ParametersJson = parameters.Json,
        }));
        Assert.Equal(ScanStatus.Finished, await newCoordinator.WaitForCompletionAsync(seed.Assessment));
        Assert.Equal(3, runs.Select(x => x.AnalysisRunId).Distinct().Count());
        using var db = data.Store.CreateContext(seed.Assessment);
        var analysisRuns = await db.AnalysisRuns.ToListAsync();
        Assert.Equal(2, analysisRuns.Count(x => x.RuleVersion == FixedSource.Rule1));
        Assert.Equal(1, analysisRuns.Count(x => x.RuleVersion == FixedSource.Rule2));
        Assert.All(analysisRuns, x =>
        {
            Assert.Equal(seed.Snapshot, x.SnapshotId);
            Assert.Equal(parameters.Json, x.ParametersJson);
            Assert.Equal(original.ManifestDigest, x.ManifestDigest);
        });
        var results = await db.AnalysisResults.ToListAsync();
        Assert.Equal(18, results.Count);
        Assert.DoesNotContain(results, x => later.Any(y => y.ObservationId == x.ObservationId));
        foreach (var expected in FixedSource.Read())
        {
            var source = await data.Store.SnapshotReader(seed.Assessment, seed.Snapshot).ReadAsync(expected.ObservationId, default);
            Assert.Equal(expected.RawBytes, source.Artifact.GetBytes());
            Assert.Equal(expected.SourceRevision, source.SourceRevision);
            foreach (var result in results.Where(x => x.ObservationId == expected.ObservationId))
            {
                var payload = new VersionedJson(result.PayloadJson).Value;
                Assert.Equal(expected.SourceRevision, payload.GetProperty("SourceRevision").GetString());
                Assert.Equal("fixed-S1", payload.GetProperty("parameters").GetProperty("configuration").GetString());
            }
        }
        Assert.Equal(original.ManifestDigest, (await data.Store.OpenSnapshotAsync(seed.Assessment, seed.Snapshot)).ManifestDigest);
        Assert.Equal(0, environment.OnlineRequests);
        Assert.Equal(0, environment.RestoreRequests);
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlRawAsync("UPDATE AnalysisRuns SET RuleVersion = 'replacement'"));
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlRawAsync("UPDATE AnalysisResults SET Reason = 'replacement'"));
    }
}
