#nullable enable
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Storage.Pipeline;
using Xunit;

namespace PnP.Scanning.Core.Tests.Pipeline;

public sealed class PipelineAnalysisTests
{
    [Fact]
    public async Task AnalysisPinsVersionsAndCannotReadUnsealedOrIncompatibleInputs()
    {
        using var data = new PipelineStoreFixture();
        await data.InitializeAsync();
        await Assert.ThrowsAsync<SnapshotIntegrityException>(() => data.StartAnalysisAsync());
        await data.Writer.SealAsync(default);
        var phase = data.Phase(Guid.NewGuid(), PhaseKind.Analysis);
        phase.InputVersion = "incompatible";
        await Assert.ThrowsAsync<ArgumentException>(() => data.Store.CreateAnalysisAsync(phase));
        var id = await data.StartAnalysisAsync("rules-v2");
        using var db = data.Store.CreateContext(data.AssessmentId, readOnly: true);
        var run = await db.AnalysisRuns.SingleAsync();
        Assert.Equal(id, run.AnalysisRunId);
        Assert.Equal("rules-v2", run.RuleVersion);
        Assert.Equal("fixture", run.ModuleKey);
        Assert.Equal((await db.SourceSnapshots.SingleAsync()).ManifestDigest, run.ManifestDigest);
    }

    [Fact]
    public async Task ResultsAreRunScopedAppendOnlyAndResumeByCommittedMembership()
    {
        using var data = new PipelineStoreFixture();
        await data.InitializeAsync();
        var observation = PipelineStoreFixture.Observation();
        await data.CommitAsync(observation);
        await data.Writer.SealAsync(default);
        var id = await data.StartAnalysisAsync();
        var writer = data.Store.ResultWriter(data.AssessmentId, id);
        Assert.False(writer is ISnapshotWriter);
        var result = new AnalysisResult(observation.ObservationId, AnalysisOutcome.Unknown,
            "Synthetic unavailable evidence", VersionedJson.From(new { classification = (string?)null }));
        await writer.WriteAsync(result, default);
        await writer.WriteAsync(result, default);
        await Assert.ThrowsAsync<SnapshotIntegrityException>(() => writer.WriteAsync(result with { Reason = "changed" }, default));
        var reopened = new PipelineStore(data.Root);
        Assert.Equal(new[] { observation.ObservationId },
            await reopened.ResultWriter(data.AssessmentId, id).GetCompletedObservationIdsAsync(default));
        Assert.Equal(result, Assert.Single(await reopened.ResultReader(data.AssessmentId, id).ReadAsync(default)));
        var secondId = await data.StartAnalysisAsync("rules-v2");
        Assert.Empty(await data.Store.ResultReader(data.AssessmentId, secondId).ReadAsync(default));
        using var db = data.Store.CreateContext(data.AssessmentId, readOnly: true);
        Assert.Equal(1, await db.AnalysisResults.CountAsync());
        var phase = await db.PhaseRuns.SingleAsync(x => x.RunId == id);
        Assert.Equal(1, phase.CompletedRecords);
        Assert.Equal(1, phase.ErrorCount);
        Assert.Equal("Synthetic unavailable evidence", (await db.AnalysisResults.SingleAsync()).Reason);
        Assert.Equal(ScanStatus.Queued, (await db.Scans.SingleAsync()).Status);
    }

    [Fact]
    public async Task ResultCommitRevalidatesSourceAfterModuleExecution()
    {
        using var data = new PipelineStoreFixture();
        await data.InitializeAsync();
        var observation = PipelineStoreFixture.Observation();
        await data.CommitAsync(observation);
        await data.Writer.SealAsync(default);
        var id = await data.StartAnalysisAsync();
        var writer = data.Store.ResultWriter(data.AssessmentId, id);
        await writer.GetCompletedObservationIdsAsync(default);
        await data.MutateAsync(async db => (await db.SourceObservations.SingleAsync()).RawBytes = [22]);
        await Assert.ThrowsAsync<SnapshotIntegrityException>(() => writer.WriteAsync(
            new(observation.ObservationId, AnalysisOutcome.Analyzed, "fixture", VersionedJson.Empty), default));
        using var db = data.Store.CreateContext(data.AssessmentId, readOnly: true);
        Assert.Empty(await db.AnalysisResults.ToListAsync());
        Assert.Equal(0, (await db.PhaseRuns.SingleAsync(x => x.RunId == id)).CompletedRecords);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReportsPublishAtomicallyAndRemainImmutableIncludingEmptyPublications(bool empty)
    {
        using var data = new PipelineStoreFixture();
        await data.InitializeAsync();
        var observation = PipelineStoreFixture.Observation();
        await data.CommitAsync(observation);
        await data.Writer.SealAsync(default);
        var id = await data.StartAnalysisAsync();
        var writer = data.Store.ReportWriter(data.AssessmentId, id);
        AnalysisReportRow[] rows = empty ? [] :
        [
            new("fixture", "second", 1, VersionedJson.From(new { value = 2 })),
            new("fixture", "first", 0, VersionedJson.From(new { value = (int?)null })),
        ];
        await Assert.ThrowsAsync<SnapshotIntegrityException>(() => writer.PublishAsync(rows, default));
        await data.Store.ResultWriter(data.AssessmentId, id).WriteAsync(
            new(observation.ObservationId, AnalysisOutcome.Analyzed, "fixture", VersionedJson.Empty), default);
        await Assert.ThrowsAsync<SnapshotIntegrityException>(() => data.Store.FinishAnalysisAsync(data.AssessmentId, id));
        await writer.PublishAsync(rows, default);
        await writer.PublishAsync(rows.Reverse().ToArray(), default);
        var changed = new[] { new AnalysisReportRow("fixture", "changed", 0, VersionedJson.Empty) };
        await Assert.ThrowsAsync<SnapshotIntegrityException>(() => writer.PublishAsync(changed, default));
        var reopened = new PipelineStore(data.Root);
        Assert.Equal(rows.OrderBy(x => x.Ordinal), await reopened.ReportReader(data.AssessmentId, id).ReadAsync(default));
        using (var db = data.Store.CreateContext(data.AssessmentId, readOnly: true))
        {
            var run = await db.AnalysisRuns.SingleAsync();
            Assert.Equal(AnalysisReportDigest.Compute(rows), run.ReportDigest);
            Assert.NotNull(run.ReportPublishedAtUtc);
        }
        await data.Store.FinishAnalysisAsync(data.AssessmentId, id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.PublishAsync(rows, default));
    }

    [Fact]
    public async Task ForeignSnapshotMembersAndBrokenReportDigestsAreRejected()
    {
        using var data = new PipelineStoreFixture();
        await data.InitializeAsync();
        await data.Writer.SealAsync(default);
        var id = await data.StartAnalysisAsync();
        await Assert.ThrowsAsync<SnapshotIntegrityException>(() => data.Store.ResultWriter(data.AssessmentId, id)
            .WriteAsync(new(Guid.NewGuid(), AnalysisOutcome.Analyzed, "fixture", VersionedJson.Empty), default));
        var rows = new[] { new AnalysisReportRow("fixture", "one", 0, VersionedJson.Empty) };
        var writer = data.Store.ReportWriter(data.AssessmentId, id);
        await Assert.ThrowsAsync<ArgumentException>(() => writer.PublishAsync([rows[0], rows[0]], default));
        await writer.PublishAsync(rows, default);
        await data.MutateAsync(async db => (await db.AnalysisReportRows.SingleAsync()).PayloadJson = VersionedJson.From(42).Json);
        await Assert.ThrowsAsync<SnapshotIntegrityException>(() => data.Store.ReportReader(data.AssessmentId, id).ReadAsync(default));
        await Assert.ThrowsAsync<SnapshotIntegrityException>(() => data.Store.FinishAnalysisAsync(data.AssessmentId, id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedResultOrReportCommitRollsBackItsCheckpointAndPublication(bool report)
    {
        using var data = new PipelineStoreFixture();
        await data.InitializeAsync();
        var observation = PipelineStoreFixture.Observation();
        await data.CommitAsync(observation);
        await data.Writer.SealAsync(default);
        var id = await data.StartAnalysisAsync();
        var result = new AnalysisResult(observation.ObservationId, AnalysisOutcome.Analyzed, "fixture", VersionedJson.Empty);
        if (report) await data.Store.ResultWriter(data.AssessmentId, id).WriteAsync(result, default);
        string? checkpoint;
        using (var db = data.Store.CreateContext(data.AssessmentId, readOnly: true))
            checkpoint = (await db.PhaseRuns.SingleAsync(x => x.RunId == id)).CheckpointJson;
        var failure = new FailAnalysisCommit(report);
        var store = new PipelineStore(data.Root, assessment => new ScanContext(new DbContextOptionsBuilder<ScanContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = data.Store.DatabasePath(assessment), Pooling = false }.ToString())
            .AddInterceptors(failure).Options));
        var rows = new[] { new AnalysisReportRow("fixture", "one", 0, VersionedJson.Empty) };
        async Task CommitAsync()
        {
            if (report) await store.ReportWriter(data.AssessmentId, id).PublishAsync(rows, default);
            else await store.ResultWriter(data.AssessmentId, id).WriteAsync(result, default);
        }
        await Assert.ThrowsAsync<IOException>(CommitAsync);
        using (var db = data.Store.CreateContext(data.AssessmentId, readOnly: true))
        {
            var phase = await db.PhaseRuns.SingleAsync(x => x.RunId == id);
            Assert.Equal(checkpoint, phase.CheckpointJson);
            Assert.Equal(report ? 1 : 0, phase.CompletedRecords);
            Assert.Equal(report ? 1 : 0, await db.AnalysisResults.CountAsync());
            Assert.Empty(await db.AnalysisReportRows.ToListAsync());
            Assert.Null((await db.AnalysisRuns.SingleAsync()).ReportDigest);
            Assert.Null((await db.AnalysisRuns.SingleAsync()).ReportPublishedAtUtc);
        }
        await CommitAsync();
        using var saved = data.Store.CreateContext(data.AssessmentId, readOnly: true);
        Assert.Equal(1, await saved.AnalysisResults.CountAsync());
        Assert.Equal(report ? 1 : 0, await saved.AnalysisReportRows.CountAsync());
    }

    private sealed class FailAnalysisCommit(bool report) : DbTransactionInterceptor
    {
        private bool failed;

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!failed && eventData.Context is ScanContext db &&
                (report ? db.AnalysisReportRows.Local.Count > 0 : db.AnalysisResults.Local.Count > 0))
            {
                failed = true;
                throw new IOException("Synthetic analysis transaction interruption.");
            }
            return ValueTask.FromResult(result);
        }
    }
}
