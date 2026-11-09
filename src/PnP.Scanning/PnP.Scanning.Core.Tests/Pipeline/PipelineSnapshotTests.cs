#nullable enable
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Data.Sqlite;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Storage.Pipeline;
using Xunit;

namespace PnP.Scanning.Core.Tests.Pipeline;

public sealed class PipelineSnapshotTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"schemaVersion\":0,\"value\":null}")]
    [InlineData("{\"schemaVersion\":\"1\",\"value\":null}")]
    [InlineData("{\"schemaVersion\":1.5,\"value\":null}")]
    [InlineData("{\"schemaVersion\":1}")]
    public void VersionedJsonRejectsInvalidEnvelopes(string json) =>
        Assert.Throws<ArgumentException>(() => new VersionedJson(json));

    [Fact]
    public void VersionedJsonPreservesVersionAndExplicitUnknownValues()
    {
        var original = VersionedJson.From(new { missing = (string?)null, unknown = (bool?)null, empty = "" }, 3);
        var reopened = new VersionedJson(original.Json);
        Assert.Equal(3, reopened.SchemaVersion);
        Assert.Equal(JsonValueKind.Null, reopened.Value.GetProperty("unknown").ValueKind);
        Assert.Equal(JsonValueKind.Null, reopened.Value.GetProperty("missing").ValueKind);
        Assert.Equal("", reopened.Value.GetProperty("empty").GetString());
        Assert.ThrowsAny<JsonException>(() => new VersionedJson("not-json"));
    }

    [Fact]
    public async Task ReopenedSnapshotPreservesEmptyAbsentAndPartialContentAndReturnsDefensiveCopies()
    {
        using var data = new PipelineStoreFixture();
        await data.InitializeAsync();
        var observations = new[]
        {
            PipelineStoreFixture.Observation(),
            PipelineStoreFixture.Observation(bytes: []),
            PipelineStoreFixture.Observation(AcquisitionStatus.Partial, [42]),
            PipelineStoreFixture.Observation(AcquisitionStatus.Denied),
            PipelineStoreFixture.Observation(AcquisitionStatus.NotAttempted),
            PipelineStoreFixture.Observation(AcquisitionStatus.NotReturned),
            PipelineStoreFixture.Observation(AcquisitionStatus.Unknown),
            PipelineStoreFixture.Observation(AcquisitionStatus.Failed),
        };
        foreach (var observation in observations) await data.CommitAsync(observation, observations.Length);
        var info = await data.Writer.SealAsync(default);
        Assert.Equal(info.ManifestDigest, (await data.Writer.SealAsync(default)).ManifestDigest);
        var reopened = new PipelineStore(data.Root);
        var reader = reopened.SnapshotReader(data.AssessmentId, data.SnapshotId);
        Assert.False(reader is ISnapshotWriter);
        Assert.Equal(info.ManifestDigest, (await reader.OpenAsync(default)).ManifestDigest);
        foreach (var expected in observations)
        {
            var actual = await reader.ReadAsync(expected.ObservationId, default);
            Assert.Equal(expected.SourceIdentity, actual.SourceIdentity);
            Assert.Equal(expected.SourceRevision, actual.SourceRevision);
            Assert.Equal(expected.AcquisitionStatus, actual.AcquisitionStatus);
            Assert.Equal(expected.Metadata.Json, actual.Metadata.Json);
            Assert.Equal(expected.AcquisitionError, actual.AcquisitionError);
            Assert.Equal(expected.RawBytes, actual.Artifact.GetBytes());
            Assert.Equal(expected.RawBytes?.LongLength, actual.Artifact.Length);
            Assert.Equal(expected.RawBytes == null ? null : SnapshotManifest.Digest(expected.RawBytes), actual.Artifact.Sha256);
        }
        var record = await reader.ReadAsync(observations[0].ObservationId, default);
        record.Artifact.GetBytes()![0] = 77;
        Assert.Equal(observations[0].RawBytes, record.Artifact.GetBytes());
        await Assert.ThrowsAsync<InvalidOperationException>(() => data.CommitAsync(PipelineStoreFixture.Observation()));
        using var db = reopened.CreateContext(data.AssessmentId, readOnly: true);
        var phase = await db.PhaseRuns.SingleAsync();
        Assert.Equal(observations.Length, phase.CompletedRecords);
        Assert.Equal(6, phase.ErrorCount);
        Assert.Equal(ScanStatus.Finished, phase.Status);
    }

    [Fact]
    public async Task ReplayIsIdempotentButCannotReplaceFactsOrReuseIdentity()
    {
        using var data = new PipelineStoreFixture();
        await data.InitializeAsync();
        var observation = PipelineStoreFixture.Observation();
        await data.CommitAsync(observation, 1);
        await data.CommitAsync(observation, 1);
        await Assert.ThrowsAsync<SnapshotIntegrityException>(() =>
            data.CommitAsync(observation with { SourceRevision = "changed" }));
        await Assert.ThrowsAsync<DbUpdateException>(() =>
            data.CommitAsync(observation with { ObservationId = Guid.NewGuid() }));
        using var db = data.Store.CreateContext(data.AssessmentId, readOnly: true);
        Assert.Equal(1, await db.SourceObservations.CountAsync());
        Assert.Equal(1, (await db.PhaseRuns.SingleAsync()).CompletedRecords);
        await data.Writer.SealAsync(default);
    }

    [Theory]
    [InlineData(false, "bytes")]
    [InlineData(false, "length")]
    [InlineData(false, "hash")]
    [InlineData(false, "metadata")]
    [InlineData(false, "missing")]
    [InlineData(false, "null")]
    [InlineData(true, "bytes")]
    [InlineData(true, "revision")]
    [InlineData(true, "identity")]
    [InlineData(true, "metadata")]
    [InlineData(true, "scope")]
    [InlineData(true, "manifest")]
    [InlineData(true, "members")]
    [InlineData(true, "missing")]
    public async Task CorruptEvidenceCannotBeSealedOrReopened(bool sealedSnapshot, string fault)
    {
        using var data = new PipelineStoreFixture();
        await data.InitializeAsync();
        var observation = PipelineStoreFixture.Observation();
        await data.CommitAsync(observation, 1);
        if (sealedSnapshot) await data.Writer.SealAsync(default);
        await data.MutateAsync(async db =>
        {
            var row = await db.SourceObservations.SingleAsync();
            var snapshot = await db.SourceSnapshots.SingleAsync();
            switch (fault)
            {
                case "bytes": row.RawBytes = [9, 8, 7]; break;
                case "length": row.Length++; break;
                case "hash": row.Sha256 = "wrong"; break;
                case "metadata": row.MetadataJson = "{\"schemaVersion\":0,\"value\":null}"; break;
                case "missing": db.SourceObservations.Remove(row); break;
                case "null": row.RawBytes = null; break;
                case "revision": row.SourceRevision = "changed"; break;
                case "identity": row.SourceIdentity = "changed"; break;
                case "scope": snapshot.ScopeJson = VersionedJson.From(new { scope = "changed" }).Json; break;
                case "manifest": snapshot.ManifestJson += " "; break;
                case "members": snapshot.MemberIdsJson = "[]"; break;
            }
        });
        if (sealedSnapshot)
            await Assert.ThrowsAsync<SnapshotIntegrityException>(() => data.Reader.OpenAsync(default));
        else
        {
            await Assert.ThrowsAsync<SnapshotIntegrityException>(() => data.Writer.SealAsync(default));
            using var db = data.Store.CreateContext(data.AssessmentId, readOnly: true);
            Assert.False((await db.SourceSnapshots.SingleAsync()).IsSealed);
            Assert.Equal(ScanStatus.Running, (await db.PhaseRuns.SingleAsync()).Status);
        }
    }

    [Fact]
    public async Task PinnedReaderRejectsSubsequentTamperingAndForeignMembers()
    {
        using var data = new PipelineStoreFixture();
        await data.InitializeAsync();
        var observation = PipelineStoreFixture.Observation();
        await data.CommitAsync(observation);
        await data.Writer.SealAsync(default);
        var reader = data.Reader;
        await reader.OpenAsync(default);
        await Assert.ThrowsAsync<SnapshotIntegrityException>(() => reader.ReadAsync(Guid.NewGuid(), default));
        await data.MutateAsync(async db => (await db.SourceObservations.SingleAsync()).MetadataJson = VersionedJson.Empty.Json);
        await Assert.ThrowsAsync<SnapshotIntegrityException>(() => reader.ReadAsync(observation.ObservationId, default));
    }

    [Fact]
    public async Task ReadersCannotCreateMigrateOrWriteDatabases()
    {
        using var missing = new PipelineStoreFixture();
        await Assert.ThrowsAsync<SqliteException>(() => missing.Reader.OpenAsync(default));
        Assert.False(Directory.Exists(missing.Root));
        using var data = new PipelineStoreFixture();
        await data.InitializeAsync();
        await data.Writer.SealAsync(default);
        var path = data.Store.DatabasePath(data.AssessmentId);
        var before = SnapshotManifest.Digest(await File.ReadAllBytesAsync(path));
        await data.Reader.OpenAsync(default);
        Assert.Single(await data.Store.GetRunsAsync(data.AssessmentId));
        using (var db = data.Store.CreateContext(data.AssessmentId, readOnly: true))
        {
            var error = await Assert.ThrowsAsync<SqliteException>(() =>
                db.Database.ExecuteSqlRawAsync("UPDATE SourceSnapshots SET IsSealed = 0"));
            Assert.Equal(8, error.SqliteErrorCode);
        }
        Assert.Equal(before, SnapshotManifest.Digest(await File.ReadAllBytesAsync(path)));
    }

    [Fact]
    public async Task FailedAndCanceledCommitsRollbackBytesProgressAndCheckpointTogether()
    {
        using var data = new PipelineStoreFixture();
        await data.InitializeAsync();
        var interceptor = new InterruptCommit();
        var store = new PipelineStore(data.Root, id => new ScanContext(new DbContextOptionsBuilder<ScanContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = data.Store.DatabasePath(id), Pooling = false }.ToString())
            .AddInterceptors(interceptor).Options));
        var writer = store.SnapshotWriter(data.AssessmentId, data.SnapshotId, data.CollectionId);
        var observation = PipelineStoreFixture.Observation();
        var record = new CollectionRecord(observation, VersionedJson.From(new { position = 1 }), 1);
        await Assert.ThrowsAsync<IOException>(() => writer.CommitAsync(record, default));
        using (var db = data.Store.CreateContext(data.AssessmentId, readOnly: true))
        {
            Assert.Empty(await db.SourceObservations.ToListAsync());
            var phase = await db.PhaseRuns.SingleAsync();
            Assert.Equal(0, phase.CompletedRecords);
            Assert.Null(phase.CheckpointJson);
        }
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.CommitAsync(record, canceled.Token));
        await writer.CommitAsync(record, default);
        Assert.Single((await writer.SealAsync(default)).ObservationIds);
    }

    [Fact]
    public async Task ConcurrentStoresSerializeSourceProgressWithoutLostUpdates()
    {
        using var data = new PipelineStoreFixture();
        await data.InitializeAsync();
        var other = new PipelineStore(data.Root).SnapshotWriter(data.AssessmentId, data.SnapshotId, data.CollectionId);
        var records = Enumerable.Range(0, 12).Select(_ => new CollectionRecord(
            PipelineStoreFixture.Observation(), VersionedJson.Empty, 12)).ToArray();
        await Task.WhenAll(records.Select((record, index) =>
            (index % 2 == 0 ? data.Writer : other).CommitAsync(record, default)));
        Assert.Equal(12, (await data.Writer.SealAsync(default)).ObservationIds.Count);
    }

    [Fact]
    public async Task CancellationAfterSqlWritesRollsBackBeforeCommit()
    {
        using var data = new PipelineStoreFixture();
        await data.InitializeAsync();
        using var cancellation = new CancellationTokenSource();
        var store = new PipelineStore(data.Root, id => new ScanContext(new DbContextOptionsBuilder<ScanContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = data.Store.DatabasePath(id), Pooling = false }.ToString())
            .AddInterceptors(new CancelAfterSave(cancellation)).Options));
        var record = new CollectionRecord(PipelineStoreFixture.Observation(), VersionedJson.From(new { position = 1 }), 1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SnapshotWriter(
            data.AssessmentId, data.SnapshotId, data.CollectionId).CommitAsync(record, cancellation.Token));
        using var db = data.Store.CreateContext(data.AssessmentId, readOnly: true);
        Assert.Empty(await db.SourceObservations.ToListAsync());
        var phase = await db.PhaseRuns.SingleAsync();
        Assert.Equal(0, phase.CompletedRecords);
        Assert.Null(phase.CheckpointJson);
    }

    private sealed class CancelAfterSave(CancellationTokenSource source) : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            source.Cancel();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class InterruptCommit : DbTransactionInterceptor
    {
        private bool failed;

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!failed && eventData.Context is ScanContext db && db.SourceObservations.Local.Count > 0)
            {
                failed = true;
                throw new IOException("Synthetic interruption after SQL writes.");
            }
            return ValueTask.FromResult(result);
        }
    }
}
