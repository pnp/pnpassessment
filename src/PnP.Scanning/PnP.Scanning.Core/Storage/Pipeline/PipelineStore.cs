#nullable enable
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Pipeline.Contracts;
using PnP.Scanning.Core.Services;

namespace PnP.Scanning.Core.Storage.Pipeline;

/// <summary>Infrastructure owns mutable entities. Each capability handed to a module is narrower.</summary>
internal sealed partial class PipelineStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, bool> migrated = new();
    private readonly Func<Guid, ScanContext>? contextFactory;

    internal PipelineStore(string rootDirectory, Func<Guid, ScanContext>? contextFactory = null)
    {
        RootDirectory = Path.GetFullPath(rootDirectory);
        this.contextFactory = contextFactory;
        Directory.CreateDirectory(RootDirectory);
    }

    internal string RootDirectory { get; }
    internal string DatabasePath(Guid assessmentId) => Path.Combine(RootDirectory, assessmentId.ToString(), StorageManager.DbName);

    internal ScanContext CreateContext(Guid assessmentId) => contextFactory?.Invoke(assessmentId) ??
        new ScanContext(new DbContextOptionsBuilder<ScanContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = DatabasePath(assessmentId), Pooling = false }.ToString())
            .Options);

    private SemaphoreSlim Gate(Guid assessmentId) => Gates.GetOrAdd(DatabasePath(assessmentId), _ => new(1, 1));

    internal async Task EnsureDatabaseAsync(Guid assessmentId, CancellationToken token, bool create = false)
    {
        if (migrated.ContainsKey(assessmentId)) return;
        if (!create && !File.Exists(DatabasePath(assessmentId)))
            throw new KeyNotFoundException($"Assessment '{assessmentId}' is unknown.");
        var gate = Gate(assessmentId);
        await gate.WaitAsync(token);
        try
        {
            if (migrated.ContainsKey(assessmentId)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath(assessmentId))!);
            using var db = CreateContext(assessmentId);
            await db.Database.MigrateAsync(token);
            migrated.TryAdd(assessmentId, true);
        }
        finally { gate.Release(); }
    }

    private async Task<T> WriteAsync<T>(Guid assessmentId, Func<ScanContext, Task<T>> action,
        CancellationToken token, bool create = false)
    {
        await EnsureDatabaseAsync(assessmentId, token, create);
        var gate = Gate(assessmentId);
        await gate.WaitAsync(token);
        try
        {
            using var db = CreateContext(assessmentId);
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
        await EnsureDatabaseAsync(assessmentId, token);
        using var db = CreateContext(assessmentId);
        return await db.PhaseRuns.AsNoTracking().OrderBy(x => x.CreatedAtUtc).ToListAsync(token);
    }

    internal async Task<PhaseRunRow?> LatestRootAsync(Guid assessmentId, CancellationToken token = default)
    {
        var rows = await GetRunsAsync(assessmentId, token);
        return rows.LastOrDefault(x => x.ParentRunId == null);
    }

    internal IEnumerable<Guid> AssessmentsOnDisk() => Directory.EnumerateDirectories(RootDirectory)
        .Select(Path.GetFileName).Where(x => Guid.TryParse(x, out _)).Select(x => Guid.Parse(x!))
        .Where(id => File.Exists(DatabasePath(id)));

    internal async Task<IReadOnlyList<PhaseRunRow>> ListRootsAsync(CancellationToken token = default)
    {
        var result = new List<PhaseRunRow>();
        foreach (var id in AssessmentsOnDisk())
            result.AddRange((await GetRunsAsync(id, token)).Where(x => x.ParentRunId == null));
        return result.OrderBy(x => x.CreatedAtUtc).ToArray();
    }

    private static void AddError(PhaseRunRow run, string message, Guid? observationId = null)
    {
        var errors = new VersionedJson(run.ErrorsJson).Value.EnumerateArray().Select(x => x.Clone()).ToList();
        errors.Add(JsonSerializer.SerializeToElement(new { atUtc = DateTime.UtcNow, observationId, message }));
        run.ErrorsJson = VersionedJson.From(errors).Json;
        run.ErrorCount++;
        run.LastError = message;
    }

    private static async Task UpdateParentAsync(ScanContext db, PhaseRunRow child, string? error = null, Guid? observationId = null)
    {
        if (child.ParentRunId is not Guid parentId) return;
        var parent = await db.PhaseRuns.SingleAsync(x => x.RunId == parentId);
        parent.CompletedRecords = child.CompletedRecords;
        parent.TotalRecords = child.TotalRecords;
        if (error != null) AddError(parent, error, observationId);
    }

    private static async Task UpdateAssessmentAsync(ScanContext db, Guid assessmentId, ScanStatus status)
    {
        var assessment = await db.Scans.SingleAsync(x => x.ScanId == assessmentId);
        assessment.Status = status;
        assessment.EndDate = status is ScanStatus.Finished or ScanStatus.Paused or ScanStatus.Terminated
            ? DateTime.UtcNow : default;
    }

    internal ISnapshotWriter SnapshotWriter(Guid assessmentId, Guid snapshotId, Guid collectionRunId) =>
        new SnapshotWriteCapability(this, assessmentId, snapshotId, collectionRunId);

    internal ISnapshotReader SnapshotReader(Guid assessmentId, Guid snapshotId) =>
        new SnapshotReadCapability(this, assessmentId, snapshotId);

    internal IAnalysisResultWriter ResultWriter(Guid assessmentId, Guid analysisRunId) =>
        new AnalysisWriteCapability(this, assessmentId, analysisRunId);

    private sealed class SnapshotWriteCapability(PipelineStore store, Guid assessmentId, Guid snapshotId, Guid collectionRunId) : ISnapshotWriter
    {
        public Task CommitAsync(CollectionRecord record, CancellationToken cancellationToken) =>
            store.CommitObservationAsync(assessmentId, snapshotId, collectionRunId, record, cancellationToken);
        public Task<SnapshotInfo> SealAsync(CancellationToken cancellationToken) =>
            store.SealAsync(assessmentId, snapshotId, collectionRunId, cancellationToken);
    }

    private sealed class SnapshotReadCapability(PipelineStore store, Guid assessmentId, Guid snapshotId) : ISnapshotReader
    {
        private PinnedManifest? pinned;
        public async Task<SnapshotInfo> OpenAsync(CancellationToken cancellationToken)
        {
            var manifest = await store.OpenManifestAsync(assessmentId, snapshotId, cancellationToken);
            var next = new PinnedManifest(manifest);
            if (pinned != null && pinned.Digest != next.Digest)
                throw new SnapshotIntegrityException("The selected manifest changed during analysis.");
            pinned = next;
            return manifest.Info();
        }
        public async Task<SourceRecord> ReadAsync(Guid observationId, CancellationToken cancellationToken)
        {
            if (pinned == null) await OpenAsync(cancellationToken);
            return await store.ReadObservationAsync(assessmentId, snapshotId, observationId, pinned!, cancellationToken);
        }
    }

    private sealed class AnalysisWriteCapability(PipelineStore store, Guid assessmentId, Guid analysisRunId) : IAnalysisResultWriter
    {
        private PinnedManifest? pinned;
        public async Task<IReadOnlySet<Guid>> GetCompletedObservationIdsAsync(CancellationToken cancellationToken)
        {
            pinned = await store.AnalysisManifestAsync(assessmentId, analysisRunId, cancellationToken);
            return await store.CompletedResultsAsync(assessmentId, analysisRunId, cancellationToken);
        }
        public async Task WriteAsync(AnalysisResult result, CancellationToken cancellationToken)
        {
            pinned ??= await store.AnalysisManifestAsync(assessmentId, analysisRunId, cancellationToken);
            await store.CommitResultAsync(assessmentId, analysisRunId, result, pinned, cancellationToken);
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
