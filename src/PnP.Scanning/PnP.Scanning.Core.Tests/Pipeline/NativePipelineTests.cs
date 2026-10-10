#nullable enable
using System.Reflection;
using System.Text.Json;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Pipeline.Orchestration;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Storage.Pipeline;
using Xunit;

namespace PnP.Scanning.Core.Tests.Pipeline;

[CollectionDefinition("Native pipeline CLI", DisableParallelization = true)]
public sealed class NativePipelineCollection;

[Collection("Native pipeline CLI")]
[Trait("Category", "Pipeline")]
public sealed class NativePipelineTests
{
    [Fact]
    public async Task Native_cli_binds_collection_options_and_runs_separate_offline_analysis_over_loopback_rpc()
    {
        using var data = new StoreCase("pipeline-native-stages");
        var control = new FixtureControl();
        var environment = new ForbiddenOnlineEnvironment();
        await using var host = await NativePipelineHost.StartAsync(data.Store, FixedSource.Registry(control), environment);
        var collected = await host.InvokeAsync("collect", "--module", FixedSource.ModuleKey,
            "--tenant", "offline.invalid", "--siteslist", "https://offline.invalid/sites/fixture",
            "--authmode", "Device", "--applicationid", "22222222-2222-2222-2222-222222222222", "--threads", "1", "--homepageonly");
        Assert.True(collected.ExitCode == 0, collected.Error);
        Assert.NotNull(collected.Ticket);
        var assessment = Guid.Parse(collected.Ticket!.AssessmentId);
        var snapshot = Guid.Parse(collected.Ticket.SnapshotId);
        Assert.Equal(ScanStatus.Finished, await host.Coordinator!.WaitForCompletionAsync(assessment));
        Assert.Equal("offline.invalid", control.LastCollectionOptions!.Tenant);
        Assert.Equal("Device", control.LastCollectionOptions.AuthMode);
        Assert.Equal("https://offline.invalid/sites/fixture", control.LastCollectionOptions.SitesList);
        Assert.Equal(1, control.LastCollectionOptions.Threads);
        Assert.Contains(control.LastCollectionOptions.Properties, x => x.Property == "homepageonly" && x.Value.Equals("True", StringComparison.OrdinalIgnoreCase));
        environment.ForbidRestore = true;
        var analyzed = await host.InvokeAsync("analyze", "--id", assessment.ToString(), "--snapshot-id", snapshot.ToString(), "--rule-version", FixedSource.Rule1);
        Assert.Equal(0, analyzed.ExitCode);
        Assert.NotNull(analyzed.Ticket);
        Assert.Equal(ScanStatus.Finished, await host.Coordinator.WaitForCompletionAsync(assessment));
        Assert.Equal(6, await data.ResultCountAsync(assessment, Guid.Parse(analyzed.Ticket!.AnalysisRunId)));
        Assert.Equal(1, control.CollectionStarts);
        Assert.Equal(1, environment.RestoreRequests);
        Assert.Equal(0, environment.OnlineRequests);
        Assert.Equal(0, environment.ProtectionRequests);
        Assert.Equal(0, host.Counter.Calls);
        var list = await host.Client.ListAsync(new ListRequest());
        var row = Assert.Single(list.Status, x => x.RunId == analyzed.Ticket.RunId);
        Assert.Equal("pipeline", row.ExecutionPath);
        Assert.Equal("Analysis", row.Phase);
        Assert.Equal(snapshot.ToString(), row.SnapshotId);
        Assert.Equal(6, row.RecordsCompleted);
        Assert.Equal(0, row.SiteCollectionsToScan);
        Assert.Equal(0, row.SiteCollectionsScanned);
        Assert.Equal(4, row.ErrorCount);
        var authParse = host.Parser.Parse(new[] { "analyze", "--id", assessment.ToString(), "--snapshot-id", snapshot.ToString(), "--tenant", "offline.invalid" });
        Assert.NotEmpty(authParse.Errors);
    }

    [Fact]
    public async Task Native_pause_close_reopen_and_restart_keep_the_original_analysis_run_and_rule()
    {
        using var data = new StoreCase("pipeline-native-resume");
        var control = new FixtureControl();
        var environment = new ForbiddenOnlineEnvironment();
        PhaseReply analysis;
        Guid assessment;
        await using (var host = await NativePipelineHost.StartAsync(data.Store, FixedSource.Registry(control), environment))
        {
            var collection = (await host.InvokeAsync("collect", "--module", FixedSource.ModuleKey, "--threads", "1")).Ticket!;
            assessment = Guid.Parse(collection.AssessmentId);
            await host.Coordinator!.WaitForCompletionAsync(assessment);
            environment.ForbidRestore = true;
            control.BlockAnalysisAt = FixedSource.Read()[1].ObservationId;
            var queued = await host.InvokeAsync("analyze", "--id", collection.AssessmentId, "--snapshot-id", collection.SnapshotId);
            Assert.True(queued.ExitCode == 0, queued.Error);
            analysis = queued.Ticket!;
            await control.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var status = await host.Client.StatusAsync(new StatusRequest());
            var row = Assert.Single(status.Status, x => x.RunId == analysis.RunId);
            Assert.Equal("Analysis", row.Phase);
            Assert.Equal(1, row.RecordsCompleted);
            Assert.Equal(0, row.SiteCollectionsScanned);
            Assert.Equal(0, (await host.InvokeAsync("pause", "--id", collection.AssessmentId)).ExitCode);
            var paused = await data.Store.LatestRootAsync(assessment);
            Assert.Equal(ScanStatus.Paused, paused!.Status);
            Assert.Equal(1, paused.CompletedRecords);
            Assert.Equal(Guid.Parse(analysis.AnalysisRunId), paused.AnalysisRunId);
        }
        control.Release();
        var reopened = new PipelineStore(data.DirectoryPath);
        await using (var missing = await NativePipelineHost.StartAsync(reopened,
            FixedSource.Registry(control, collector: false, current: FixedSource.Rule2, rule1: false), environment))
        {
            var messages = await ReadRestartAsync(missing.Client, assessment);
            Assert.Contains(messages, x => x.Type == Constants.MessageError && x.Status.Contains(FixedSource.Rule1, StringComparison.Ordinal));
            Assert.Equal(1, (await missing.InvokeAsync("restart", "--id", assessment.ToString())).ExitCode);
            Assert.Equal(ScanStatus.Paused, (await reopened.LatestRootAsync(assessment))!.Status);
        }
        await using (var resumed = await NativePipelineHost.StartAsync(new PipelineStore(data.DirectoryPath),
            FixedSource.Registry(control, collector: false, current: FixedSource.Rule2), environment))
        {
            Assert.Equal(0, (await resumed.InvokeAsync("restart", "--id", assessment.ToString())).ExitCode);
            Assert.Equal(ScanStatus.Finished, await resumed.Coordinator!.WaitForCompletionAsync(assessment));
            Assert.Equal(6, await data.ResultCountAsync(assessment, Guid.Parse(analysis.AnalysisRunId)));
            Assert.Equal(0, resumed.Counter.Calls);
        }
        using var db = reopened.CreateContext(assessment);
        var run = Assert.Single(await db.AnalysisRuns.ToListAsync());
        Assert.Equal(Guid.Parse(analysis.AnalysisRunId), run.AnalysisRunId);
        Assert.Equal(FixedSource.Rule1, run.RuleVersion);
        Assert.Equal(1, control.AnalysisCalls[(FixedSource.Rule1, FixedSource.Read()[0].ObservationId)]);
        Assert.Equal(0, control.CollectionStarts - 1);
        Assert.Equal(1, environment.RestoreRequests);
        Assert.Equal(0, environment.OnlineRequests);
        Assert.Equal(0, environment.ProtectionRequests);
    }

    [Fact]
    public async Task New_pipeline_rpc_is_not_downgraded_to_start_on_an_old_service()
    {
        using var data = new StoreCase("pipeline-old-service");
        var environment = new ForbiddenOnlineEnvironment();
        await using var old = await NativePipelineHost.StartAsync(data.Store, new ModuleRegistry(), environment, legacyOnly: true);
        var result = await old.InvokeAsync("start", "--module", FixedSource.ModuleKey, "--threads", "1");
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("does not support", result.Error, StringComparison.Ordinal);
        Assert.Equal(0, old.Legacy!.StartCalls);
        Assert.Equal(1, (await old.InvokeAsync("stop", "--id", Guid.NewGuid().ToString())).ExitCode);
        Assert.Equal(0, old.Legacy.StopCalls);
        Assert.Equal(0, environment.ProtectionRequests);
        Assert.Empty(data.Store.AssessmentsOnDisk());
    }

    [Fact]
    public async Task Unregistered_modules_rules_and_incompatible_inputs_are_rejected_before_collection()
    {
        using var data = new StoreCase("pipeline-preflight");
        var environment = new ForbiddenOnlineEnvironment();
        var control = new FixtureControl();
        await using (var host = await NativePipelineHost.StartAsync(data.Store, new ModuleRegistry(), environment))
        {
            var unsupported = await host.InvokeAsync("collect", "--mode", "Classic", "--threads", "1");
            Assert.Equal(1, unsupported.ExitCode);
            Assert.Contains("legacy", unsupported.Error, StringComparison.Ordinal);
            var start = await host.InvokeAsync("start", "--module", "missing", "--threads", "1");
            Assert.Equal(1, start.ExitCode);
            Assert.Empty(data.Store.AssessmentsOnDisk());
            Assert.Equal(0, host.Counter.Calls);
        }
        await using (var host = await NativePipelineHost.StartAsync(data.Store, FixedSource.Registry(control), environment))
        {
            Assert.Equal(1, (await host.InvokeAsync("start", "--module", FixedSource.ModuleKey,
                "--rule-version", "missing", "--threads", "1")).ExitCode);
            Assert.Empty(data.Store.AssessmentsOnDisk());
        }
        var incompatible = new ModuleRegistry(
            [new(FixedSource.ModuleKey, FixedSource.InputVersion, () => new FixtureCollector(control))],
            [new(FixedSource.ModuleKey, FixedSource.Rule1, ["fixture/v2"], () => control.CreateAnalyzer(FixedSource.Rule1))],
            new Dictionary<string, string> { [FixedSource.ModuleKey] = FixedSource.Rule1 });
        await using (var host = await NativePipelineHost.StartAsync(data.Store, incompatible, environment))
        {
            Assert.Equal(1, (await host.InvokeAsync("start", "--module", FixedSource.ModuleKey, "--threads", "1")).ExitCode);
            Assert.Empty(data.Store.AssessmentsOnDisk());
        }
        Assert.Equal(0, control.CollectionStarts);
        Assert.Equal(0, control.AnalysisFactories);
        Assert.Equal(0, environment.RestoreRequests);
        Assert.Equal(0, environment.ProtectionRequests);
        Assert.Equal(0, environment.OnlineRequests);
    }

    [Fact]
    public async Task Native_stop_terminates_one_run_without_stopping_the_engine_and_restart_resumes_it()
    {
        using var data = new StoreCase("pipeline-stop");
        var control = new FixtureControl { BlockAnalysisAt = FixedSource.Read()[1].ObservationId };
        var environment = new ForbiddenOnlineEnvironment();
        await using var host = await NativePipelineHost.StartAsync(data.Store, FixedSource.Registry(control), environment);
        var start = await host.InvokeAsync("start", "--module", FixedSource.ModuleKey, "--threads", "1");
        Assert.True(start.ExitCode == 0, start.Error);
        var ticket = start.Ticket!;
        var assessment = Guid.Parse(ticket.AssessmentId);
        await control.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, (await host.InvokeAsync("stop", "--id", ticket.AssessmentId)).ExitCode);
        Assert.Equal(ScanStatus.Terminated, (await data.Store.LatestRootAsync(assessment))!.Status);
        Assert.True((await host.Client.PingAsync(new Google.Protobuf.WellKnownTypes.Empty())).UpAndRunning);
        environment.ForbidRestore = true;
        control.Release();
        Assert.Equal(0, (await host.InvokeAsync("restart", "--id", ticket.AssessmentId)).ExitCode);
        Assert.Equal(ScanStatus.Finished, await host.Coordinator!.WaitForCompletionAsync(assessment));
        Assert.Equal(1, control.CollectionStarts);
        using var db = data.Store.CreateContext(assessment);
        Assert.Equal(6, await db.AnalysisResults.CountAsync());
        Assert.Equal(1, await db.AnalysisRuns.CountAsync());
        Assert.Contains("canceled", (await db.PhaseRuns.SingleAsync(x => x.ParentRunId == null)).ErrorsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Legacy_start_rpc_and_cli_selection_remain_distinct_from_pipeline_selection()
    {
        using var data = new StoreCase("pipeline-legacy-start");
        var environment = new ForbiddenOnlineEnvironment();
        await using var host = await NativePipelineHost.StartAsync(data.Store, new ModuleRegistry(), environment);
        var parsed = host.Parser.Parse(new[] { "start", "--mode", "Classic" });
        Assert.NotEmpty(parsed.Errors);
        using var call = host.Client.Start(new StartRequest
        {
            Mode = "Classic", Environment = "Production", Tenant = "offline.invalid", AuthMode = "InvalidFixtureAuth",
            ApplicationId = "22222222-2222-2222-2222-222222222222", Threads = 1,
        });
        var messages = new List<StartStatus>();
        await foreach (var message in call.ResponseStream.ReadAllAsync()) messages.Add(message);
        Assert.Contains(messages, x => x.Status.Contains("legacy", StringComparison.Ordinal));
        Assert.Contains(messages, x => x.Type == Constants.MessageError && x.Status.Contains("InvalidFixtureAuth", StringComparison.Ordinal));
        Assert.Empty(data.Store.AssessmentsOnDisk());
        Assert.Equal(0, environment.OnlineRequests);
    }

    [Fact]
    public async Task Native_start_reopen_and_offline_reanalysis_preserve_source_and_create_independent_results()
    {
        using var data = new StoreCase("pipeline-native-acceptance");
        var control = new FixtureControl();
        var environment = new ForbiddenOnlineEnvironment();
        PhaseReply combined;
        PhaseReply reanalyzed;
        Guid assessment;
        await using (var host = await NativePipelineHost.StartAsync(data.Store, FixedSource.Registry(control), environment))
        {
            var started = await host.InvokeAsync("start", "--module", FixedSource.ModuleKey, "--threads", "2");
            Assert.True(started.ExitCode == 0, started.Error);
            combined = started.Ticket!;
            assessment = Guid.Parse(combined.AssessmentId);
            Assert.Equal(ScanStatus.Finished, await host.Coordinator!.WaitForCompletionAsync(assessment));
            Assert.Equal(0, host.Counter.Calls);
        }
        environment.ForbidRestore = true;
        var reopened = new PipelineStore(data.DirectoryPath);
        var before = await reopened.OpenSnapshotAsync(assessment, Guid.Parse(combined.SnapshotId));
        await using (var host = await NativePipelineHost.StartAsync(reopened, FixedSource.Registry(control, collector: false), environment))
        {
            var result = await host.InvokeAsync("analyze", "--id", combined.AssessmentId, "--snapshot-id", combined.SnapshotId,
                "--rule-version", FixedSource.Rule2);
            Assert.Equal(0, result.ExitCode);
            reanalyzed = result.Ticket!;
            Assert.Equal(ScanStatus.Finished, await host.Coordinator!.WaitForCompletionAsync(assessment));
            Assert.Equal(0, host.Counter.Calls);
        }
        var after = await reopened.OpenSnapshotAsync(assessment, before.SnapshotId);
        Assert.Equal(before.ManifestDigest, after.ManifestDigest);
        Assert.NotEqual(combined.AnalysisRunId, reanalyzed.AnalysisRunId);
        Assert.Equal(1, control.CollectionStarts);
        Assert.Equal(0, environment.ProtectionRequests);
        Assert.Equal(0, environment.OnlineRequests);
        using var db = reopened.CreateContext(assessment);
        var phases = await db.PhaseRuns.AsNoTracking().OrderBy(x => x.CreatedAtUtc).ToListAsync();
        Assert.Equal(4, phases.Count);
        Assert.All(phases, x => Assert.Equal(ScanStatus.Finished, x.Status));
        var results = await db.AnalysisResults.AsNoTracking().OrderBy(x => x.AnalysisRunId).ThenBy(x => x.ObservationId).ToListAsync();
        Assert.Equal(12, results.Count);
        Assert.Equal(8, results.Count(x => x.Outcome == AnalysisOutcome.Unknown));
        var observations = await db.SourceObservations.AsNoTracking().OrderBy(x => x.ObservationId).ToListAsync();
        foreach (var expected in FixedSource.Read())
        {
            var observation = observations.Single(x => x.ObservationId == expected.ObservationId);
            Assert.Equal(expected.RawBytes, observation.RawBytes);
            foreach (var result in results.Where(x => x.ObservationId == expected.ObservationId))
            {
                var value = new VersionedJson(result.PayloadJson).Value;
                Assert.Equal(expected.RawBytes == null ? null : Convert.ToHexString(expected.RawBytes).ToLowerInvariant(), value.GetProperty("rawHex").GetString());
                Assert.Equal(expected.SourceRevision, value.GetProperty("SourceRevision").GetString());
            }
        }
    }

    private static async Task<List<RestartStatus>> ReadRestartAsync(PnPScanner.PnPScannerClient client, Guid assessment)
    {
        using var call = client.Restart(new RestartRequest { Id = assessment.ToString() });
        var messages = new List<RestartStatus>();
        await foreach (var message in call.ResponseStream.ReadAllAsync()) messages.Add(message);
        return messages;
    }
}
