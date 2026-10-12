#nullable enable
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Pipeline.Analysis.Module;
using PnP.Scanning.Core.Pipeline.Collection.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Page;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Pipeline.Orchestration;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using Xunit;

namespace PnP.Scanning.Core.Tests.Pipeline;

/// <summary>
/// Verifies report immutability, source corruption rejection, reference validation and finalization recovery.
/// </summary>
[Trait("Category", "ClassicPagePipeline")]
public sealed class ClassicPageIntegrityTests
{
    /// <summary>
    /// Verifies report immutability and rejection of export or reanalysis when sealed source content is corrupted.
    /// </summary>
    [Fact]
    public async Task Reports_are_immutable_and_source_corruption_rejects_report_and_reanalysis()
    {
        using var data = new StoreCase("classicpage-corruption"); var fixture = new ClassicPageFixture();
        var coordinator = new PipelineCoordinator(data.Store, ClassicPageModule.Registry(fixture.OpenAsync), new ForbiddenOnlineEnvironment());
        var ticket = await coordinator.StartPipelineAsync(new() { Collection = ClassicPageFixture.Request() });
        var id = Guid.Parse(ticket.AssessmentId); Assert.Equal(ScanStatus.Finished, await coordinator.WaitForCompletionAsync(id));
        using var db = data.Store.CreateContext(id);
        var corruptedPayload = "{}";
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE ClassicPageReportRows SET PayloadJson = {corruptedPayload}"));
        var observation = await db.SourceObservations.FirstAsync(x => x.Length > 0);
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER Pipeline_SourceObservations_sealed_UPDATE");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE SourceObservations SET RawBytes = X'01' WHERE ObservationId = {observation.ObservationId}");
        await Assert.ThrowsAsync<SnapshotIntegrityException>(() => coordinator.AnalyzeAsync(new() { Id = ticket.AssessmentId, SnapshotId = ticket.SnapshotId }));
        var path = System.IO.Path.Combine(data.DirectoryPath, "corrupt-report");
        await Assert.ThrowsAsync<SnapshotIntegrityException>(() => ClassicPageReportExporter.ExportAsync(data.Store, id, null, path, ",", false, default));
        Assert.False(Directory.Exists(path)); Assert.Equal(1, await db.AnalysisRuns.CountAsync());
    }

    /// <summary>
    /// Verifies that foreign page references and incomplete audit-window coverage cannot pass input validation.
    /// </summary>
    [Fact]
    public async Task Page_reference_mismatch_and_unsettled_audit_window_cannot_be_accepted()
    {
        using var data = new StoreCase("classicpage-references"); var fixture = new ClassicPageFixture();
        var coordinator = new PipelineCoordinator(data.Store, ClassicPageModule.Registry(fixture.OpenAsync), new ForbiddenOnlineEnvironment());
        var ticket = await coordinator.CollectAsync(ClassicPageFixture.Request()); var id = Guid.Parse(ticket.AssessmentId);
        Assert.Equal(ScanStatus.Finished, await coordinator.WaitForCompletionAsync(id));
        var reader = data.Store.SnapshotReader(id, Guid.Parse(ticket.SnapshotId)); var manifest = await reader.OpenAsync(default);
        var records = new List<SourceRecord>(); foreach (var member in manifest.ObservationIds) records.Add(await reader.ReadAsync(member, default));
        var pageIndex = records.FindIndex(x => ClassicPageSourceJson.Kind(x) == "Page"); var original = records[pageIndex];
        var input = ClassicPageSourceJson.Read<ClassicPageItemSource>(original.Artifact.GetBytes()) with { ListId = Guid.NewGuid() };
        records[pageIndex] = original with { Artifact = new SourceArtifact(null, null, ClassicPageSourceJson.Bytes(input)) };
        // Feed the modified detached records through the production input index without storage writes.
        async IAsyncEnumerable<SourceRecord> Read() { foreach (var record in records) { yield return record; await Task.CompletedTask; } }
        await Assert.ThrowsAsync<SnapshotIntegrityException>(() => ClassicPageInputIndex.CreateAsync(Read(), default));
        records[pageIndex] = original;
        var scopeIndex = records.FindIndex(x => ClassicPageSourceJson.Kind(x) == "Scope"); var scopeRecord = records[scopeIndex];
        var scope = ClassicPageSourceJson.Read<ClassicPageScopeSource>(scopeRecord.Artifact.GetBytes());
        scope = scope with { Options = scope.Options with { SkipUsageInformation = false } };
        records[scopeIndex] = scopeRecord with { Artifact = new SourceArtifact(null, null, ClassicPageSourceJson.Bytes(scope)) };
        await Assert.ThrowsAsync<SnapshotIntegrityException>(() => ClassicPageInputIndex.CreateAsync(Read(), default));
    }

    /// <summary>
    /// Verifies finalizer recovery after publication without replacing committed results or report rows.
    /// </summary>
    [Fact]
    public async Task Pause_after_report_publication_resumes_finalization_without_rewriting_results_or_reports()
    {
        using var data = new StoreCase("classicpage-finalizer"); var fixture = new ClassicPageFixture();
        var control = new FinalizerControl(); var basis = ClassicPageModule.Registry(fixture.OpenAsync);
        var registry = new ModuleRegistry([basis.GetCollector("classicpage")],
            [basis.GetAnalyzer("classicpage") with { Create = () => control.Create() }], new Dictionary<string, string> { ["classicpage"] = "classicpage-v1" });
        var environment = new ForbiddenOnlineEnvironment();
        var coordinator = new PipelineCoordinator(data.Store, registry, environment);
        var ticket = await coordinator.StartPipelineAsync(new() { Collection = ClassicPageFixture.Request() }); var id = Guid.Parse(ticket.AssessmentId);
        await control.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await coordinator.PauseAsync(id, false);
        using var db = data.Store.CreateContext(id);
        var resultCount = await db.AnalysisResults.CountAsync(); var reportCount = await db.ClassicPageReportRows.CountAsync();
        Assert.True(reportCount > 0); Assert.Equal(ScanStatus.Paused, (await data.Store.LatestRootAsync(id))!.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ClassicPageReportExporter.ExportAsync(data.Store, id, Guid.Parse(ticket.AnalysisRunId), null, ",", false, default));
        control.Block = false; environment.ForbidRestore = true;
        var reopened = new PipelineCoordinator(data.Store, registry, environment);
        await reopened.RestartAsync(id);
        Assert.Equal(ScanStatus.Finished, await reopened.WaitForCompletionAsync(id));
        Assert.Equal(resultCount, await db.AnalysisResults.CountAsync()); Assert.Equal(reportCount, await db.ClassicPageReportRows.CountAsync());
        Assert.Equal(resultCount, control.AnalysisCalls);
        Assert.Equal(1, fixture.Calls["sites"]); Assert.Equal(1, environment.RestoreRequests);
    }

    /// <summary>
    /// Controls a pause immediately after report publication and counts per-source analysis calls.
    /// </summary>
    private sealed class FinalizerControl
    {
        internal bool Block = true;
        internal int AnalysisCalls;
        internal TaskCompletionSource Reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>
        /// Creates the controlled wrapper around the production Classic Pages analyzer.
        /// </summary>
        internal IAnalysisModule Create() => new Module(this);
        /// <summary>
        /// Delegates production page analysis while allowing finalization to block after publishing reports.
        /// </summary>
        private sealed class Module(FinalizerControl owner) : IAnalysisModule, IAnalysisSnapshotPreparation, IAnalysisFinalizer
        {
            private readonly ClassicPageAnalysisModule inner = new();
            public string ModuleKey => inner.ModuleKey;
            public string RuleVersion => inner.RuleVersion;
            /// <summary>
            /// Delegates snapshot preparation to the production analyzer.
            /// </summary>
            public Task PrepareAsync(ISnapshotReader reader, CancellationToken token) => inner.PrepareAsync(reader, token);
            /// <summary>
            /// Counts each source analysis and delegates its result to the production analyzer.
            /// </summary>
            public Task<AnalysisResult> AnalyzeAsync(SourceRecord source, VersionedJson parameters, CancellationToken token)
            { Interlocked.Increment(ref owner.AnalysisCalls); return inner.AnalyzeAsync(source, parameters, token); }
            /// <summary>
            /// Publishes the production report and optionally waits for cancellation to verify finalizer restart.
            /// </summary>
            public async Task FinalizeAsync(ISnapshotReader reader, IAnalysisResultReader results, IAnalysisReportWriter reports, VersionedJson parameters, CancellationToken token)
            {
                await inner.FinalizeAsync(reader, results, reports, parameters, token);
                if (owner.Block) { owner.Reached.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            }
        }
    }
}
