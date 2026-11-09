#nullable enable
using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Pipeline.Contracts.Module;

namespace PnP.Scanning.Core.Storage.Pipeline;

/// <summary>Assessment-scoped persistence; it does not schedule phases or select runtime modules.</summary>
internal sealed partial class PipelineStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, bool> migrated = new();
    private readonly Func<Guid, ScanContext>? writeContextFactory;

    internal PipelineStore(string rootDirectory, Func<Guid, ScanContext>? writeContextFactory = null)
    {
        RootDirectory = Path.GetFullPath(rootDirectory);
        this.writeContextFactory = writeContextFactory;
    }

    internal string RootDirectory { get; }

    internal string DatabasePath(Guid assessmentId)
    {
        if (assessmentId == Guid.Empty) throw new ArgumentException("An assessment ID is required.", nameof(assessmentId));
        return Path.Combine(RootDirectory, assessmentId.ToString(), StorageManager.DbName);
    }

    internal ScanContext CreateContext(Guid assessmentId, bool readOnly = false) =>
        !readOnly && writeContextFactory != null ? writeContextFactory(assessmentId) :
        new ScanContext(new DbContextOptionsBuilder<ScanContext>().UseSqlite(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath(assessmentId),
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString()).Options);

    private async Task<T> WriteAsync<T>(Guid assessmentId, Func<ScanContext, Task<T>> action,
        CancellationToken token, bool create = false)
    {
        var path = DatabasePath(assessmentId);
        var gate = Gates.GetOrAdd(path, _ => new(1, 1));
        await gate.WaitAsync(token);
        try
        {
            if (!create && !File.Exists(path)) throw new KeyNotFoundException($"Assessment '{assessmentId}' is unknown.");
            if (create) Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var db = CreateContext(assessmentId);
            if (!migrated.ContainsKey(assessmentId))
            {
                await db.Database.MigrateAsync(token);
                migrated.TryAdd(assessmentId, true);
            }
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var result = await action(db);
            await db.SaveChangesAsync(token);
            token.ThrowIfCancellationRequested();
            await transaction.CommitAsync(token);
            return result;
        }
        finally { gate.Release(); }
    }

    internal async Task<IReadOnlyList<PhaseRunRow>> GetRunsAsync(Guid assessmentId, CancellationToken token = default)
    {
        using var db = CreateContext(assessmentId, readOnly: true);
        return await db.PhaseRuns.AsNoTracking().Where(x => x.AssessmentId == assessmentId)
            .OrderBy(x => x.CreatedAtUtc).ToListAsync(token);
    }

    internal ISnapshotWriter SnapshotWriter(Guid assessmentId, Guid snapshotId, Guid collectionRunId) =>
        new SnapshotWriteCapability(this, assessmentId, snapshotId, collectionRunId);

    internal ISnapshotReader SnapshotReader(Guid assessmentId, Guid snapshotId) =>
        new SnapshotReadCapability(this, assessmentId, snapshotId);

    internal IAnalysisResultWriter ResultWriter(Guid assessmentId, Guid analysisRunId) =>
        new AnalysisWriteCapability(this, assessmentId, analysisRunId);

    private sealed class SnapshotWriteCapability(PipelineStore store, Guid assessmentId, Guid snapshotId, Guid runId) : ISnapshotWriter
    {
        public Task CommitAsync(CollectionRecord record, CancellationToken cancellationToken) =>
            store.CommitObservationAsync(assessmentId, snapshotId, runId, record, cancellationToken);
        public Task<SnapshotInfo> SealAsync(CancellationToken cancellationToken) =>
            store.SealAsync(assessmentId, snapshotId, runId, cancellationToken);
    }

    private sealed class SnapshotReadCapability(PipelineStore store, Guid assessmentId, Guid snapshotId) : ISnapshotReader
    {
        private PinnedManifest? pinned;

        public async Task<SnapshotInfo> OpenAsync(CancellationToken cancellationToken)
        {
            var next = new PinnedManifest(await store.OpenManifestAsync(assessmentId, snapshotId, cancellationToken));
            if (pinned != null && pinned.Digest != next.Digest)
                throw new SnapshotIntegrityException("The selected manifest changed during analysis.");
            pinned = next;
            return next.Manifest.Info();
        }

        public async Task<SourceRecord> ReadAsync(Guid observationId, CancellationToken cancellationToken)
        {
            if (pinned == null) await OpenAsync(cancellationToken);
            using var db = store.CreateContext(assessmentId, readOnly: true);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var snapshot = await db.SourceSnapshots.AsNoTracking()
                .SingleAsync(x => x.SnapshotId == snapshotId && x.AssessmentId == assessmentId, cancellationToken);
            return await ReadSelectedObservationAsync(db, snapshot, observationId, pinned!, cancellationToken);
        }
    }

    private sealed class AnalysisWriteCapability(PipelineStore store, Guid assessmentId, Guid runId) : IAnalysisResultWriter
    {
        private PinnedManifest? pinned;

        public async Task<IReadOnlySet<Guid>> GetCompletedObservationIdsAsync(CancellationToken cancellationToken)
        {
            using var db = store.CreateContext(assessmentId, readOnly: true);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var next = await AnalysisManifestAsync(db, runId, cancellationToken);
            if (pinned != null && pinned.Digest != next.Digest)
                throw new SnapshotIntegrityException("The analysis input changed.");
            pinned = next;
            return await CompletedResultsAsync(db, runId, cancellationToken);
        }

        public async Task WriteAsync(AnalysisResult result, CancellationToken cancellationToken)
        {
            if (pinned == null)
            {
                using var db = store.CreateContext(assessmentId, readOnly: true);
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                pinned = await AnalysisManifestAsync(db, runId, cancellationToken);
            }
            await store.WriteAsync(assessmentId, db =>
                CommitResultAsync(db, runId, result, pinned, cancellationToken), cancellationToken);
        }
    }

    private sealed class PinnedManifest
    {
        internal PinnedManifest(SnapshotManifest manifest)
        {
            Manifest = manifest;
            Json = manifest.Serialize();
            Digest = SnapshotManifest.Digest(Json);
            MemberIdsJson = manifest.MemberIds();
            Members = manifest.Members.ToDictionary(x => x.ObservationId);
        }

        internal SnapshotManifest Manifest { get; }
        internal string Json { get; }
        internal string Digest { get; }
        internal string MemberIdsJson { get; }
        internal IReadOnlyDictionary<Guid, ManifestMember> Members { get; }
    }
}
