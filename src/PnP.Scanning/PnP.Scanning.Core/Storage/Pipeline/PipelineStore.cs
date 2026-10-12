#nullable enable
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Services;

namespace PnP.Scanning.Core.Storage.Pipeline;

/// <summary>
/// Owns per-assessment SQLite access, transactions and integrity validation while exposing narrow collection and analysis capabilities.
/// </summary>
internal sealed partial class PipelineStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, bool> migrated = new();
    private readonly Func<Guid, ScanContext>? contextFactory;

    /// <summary>
    /// Binds the assessment root directory and an optional context factory used to verify storage failure behavior.
    /// </summary>
    internal PipelineStore(string rootDirectory, Func<Guid, ScanContext>? contextFactory = null)
    {
        RootDirectory = Path.GetFullPath(rootDirectory);
        this.contextFactory = contextFactory;
        Directory.CreateDirectory(RootDirectory);
    }

    internal string RootDirectory { get; }
    /// <summary>
    /// Returns the existing assessment.db path under the selected assessment directory.
    /// </summary>
    internal string DatabasePath(Guid assessmentId) => Path.Combine(RootDirectory, assessmentId.ToString(), StorageManager.DbName);

    /// <summary>
    /// Creates an assessment-bound SQLite context without connection pooling, or uses the injected context factory.
    /// </summary>
    internal ScanContext CreateContext(Guid assessmentId) => contextFactory?.Invoke(assessmentId) ??
        new ScanContext(new DbContextOptionsBuilder<ScanContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = DatabasePath(assessmentId), Pooling = false }.ToString())
            .Options);

    /// <summary>
    /// Returns the shared writer semaphore for the assessment database path.
    /// </summary>
    private SemaphoreSlim Gate(Guid assessmentId) => Gates.GetOrAdd(DatabasePath(assessmentId), _ => new(1, 1));

    /// <summary>
    /// Creates or opens an assessment database, applies migrations once per store instance and rejects unknown inputs unless creation is requested.
    /// </summary>
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

    /// <summary>
    /// Serializes assessment writes and commits entity changes in one cancellation-aware SQLite transaction.
    /// </summary>
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

    /// <summary>
    /// Reads the persisted phases for an assessment in creation order without tracking mutable entities.
    /// </summary>
    internal async Task<IReadOnlyList<PhaseRunRow>> GetRunsAsync(Guid assessmentId, CancellationToken token = default)
    {
        await EnsureDatabaseAsync(assessmentId, token);
        using var db = CreateContext(assessmentId);
        return await db.PhaseRuns.AsNoTracking().OrderBy(x => x.CreatedAtUtc).ToListAsync(token);
    }

    /// <summary>
    /// Returns the most recently created root collection, analysis or combined run.
    /// </summary>
    internal async Task<PhaseRunRow?> LatestRootAsync(Guid assessmentId, CancellationToken token = default)
    {
        var rows = await GetRunsAsync(assessmentId, token);
        return rows.LastOrDefault(x => x.ParentRunId == null);
    }

    /// <summary>
    /// Enumerates local assessment directories that contain the existing SQLite database file.
    /// </summary>
    internal IEnumerable<Guid> AssessmentsOnDisk() => Directory.EnumerateDirectories(RootDirectory)
        .Select(Path.GetFileName).Where(x => Guid.TryParse(x, out _)).Select(x => Guid.Parse(x!))
        .Where(id => File.Exists(DatabasePath(id)));

    /// <summary>
    /// Reads all local root runs and returns them in creation order.
    /// </summary>
    internal async Task<IReadOnlyList<PhaseRunRow>> ListRootsAsync(CancellationToken token = default)
    {
        var result = new List<PhaseRunRow>();
        foreach (var id in AssessmentsOnDisk())
            result.AddRange((await GetRunsAsync(id, token)).Where(x => x.ParentRunId == null));
        return result.OrderBy(x => x.CreatedAtUtc).ToArray();
    }

    /// <summary>
    /// Appends a timestamped diagnostic and optional observation identity, updating the error count and latest error.
    /// </summary>
    private static void AddError(PhaseRunRow run, string message, Guid? observationId = null)
    {
        var errors = new VersionedJson(run.ErrorsJson).Value.EnumerateArray().Select(x => x.Clone()).ToList();
        errors.Add(JsonSerializer.SerializeToElement(new { atUtc = DateTime.UtcNow, observationId, message }));
        run.ErrorsJson = VersionedJson.From(errors).Json;
        run.ErrorCount++;
        run.LastError = message;
    }

    /// <summary>
    /// Propagates child record progress and a new diagnostic to the combined parent run.
    /// </summary>
    private static async Task UpdateParentAsync(ScanContext db, PhaseRunRow child, string? error = null, Guid? observationId = null)
    {
        if (child.ParentRunId is not Guid parentId) return;
        var parent = await db.PhaseRuns.SingleAsync(x => x.RunId == parentId);
        parent.CompletedRecords = child.CompletedRecords;
        parent.TotalRecords = child.TotalRecords;
        if (error != null) AddError(parent, error, observationId);
    }

    /// <summary>
    /// Mirrors phase settlement into the existing assessment status and end-time fields.
    /// </summary>
    private static async Task UpdateAssessmentAsync(ScanContext db, Guid assessmentId, ScanStatus status)
    {
        var assessment = await db.Scans.SingleAsync(x => x.ScanId == assessmentId);
        assessment.Status = status;
        assessment.EndDate = status is ScanStatus.Finished or ScanStatus.Paused or ScanStatus.Terminated
            ? DateTime.UtcNow : default;
    }

    /// <summary>
    /// Creates a source-write capability bound to one assessment, unsealed snapshot and collection run.
    /// </summary>
    internal ISnapshotWriter SnapshotWriter(Guid assessmentId, Guid snapshotId, Guid collectionRunId) =>
        new SnapshotWriteCapability(this, assessmentId, snapshotId, collectionRunId);

    /// <summary>
    /// Creates a read-only capability bound to one explicitly selected snapshot.
    /// </summary>
    internal ISnapshotReader SnapshotReader(Guid assessmentId, Guid snapshotId) =>
        new SnapshotReadCapability(this, assessmentId, snapshotId);

    /// <summary>
    /// Creates an append-only analysis result capability bound to one run.
    /// </summary>
    internal IAnalysisResultWriter ResultWriter(Guid assessmentId, Guid analysisRunId) =>
        new AnalysisWriteCapability(this, assessmentId, analysisRunId);

    /// <summary>
    /// Restricts source commits and sealing to the selected collection run and snapshot.
    /// </summary>
    private sealed class SnapshotWriteCapability(PipelineStore store, Guid assessmentId, Guid snapshotId, Guid collectionRunId) : ISnapshotWriter
    {
        /// <summary>
        /// Commits the supplied observation and collector checkpoint through the bound collection transaction.
        /// </summary>
        public Task CommitAsync(CollectionRecord record, CancellationToken cancellationToken) =>
            store.CommitObservationAsync(assessmentId, snapshotId, collectionRunId, record, cancellationToken);
        /// <summary>
        /// Validates saved observations and publishes the fixed manifest for this collection snapshot.
        /// </summary>
        public Task<SnapshotInfo> SealAsync(CancellationToken cancellationToken) =>
            store.SealAsync(assessmentId, snapshotId, collectionRunId, cancellationToken);
    }

    /// <summary>
    /// Pins and validates one sealed manifest and exposes detached source reads without any write capability.
    /// </summary>
    private sealed class SnapshotReadCapability(PipelineStore store, Guid assessmentId, Guid snapshotId) : ISnapshotReader
    {
        private PinnedManifest? pinned;
        /// <summary>
        /// Validates the selected sealed manifest and rejects a changed manifest after the reader has pinned it.
        /// </summary>
        public async Task<SnapshotInfo> OpenAsync(CancellationToken cancellationToken)
        {
            var manifest = await store.OpenManifestAsync(assessmentId, snapshotId, cancellationToken);
            var next = new PinnedManifest(manifest);
            if (pinned != null && pinned.Digest != next.Digest)
                throw new SnapshotIntegrityException("The selected manifest changed during analysis.");
            pinned = next;
            return manifest.Info();
        }
        /// <summary>
        /// Reads one observation against the pinned manifest, opening the snapshot first when necessary.
        /// </summary>
        public async Task<SourceRecord> ReadAsync(Guid observationId, CancellationToken cancellationToken)
        {
            if (pinned == null) await OpenAsync(cancellationToken);
            return await store.ReadObservationAsync(assessmentId, snapshotId, observationId, pinned!, cancellationToken);
        }
    }

    /// <summary>
    /// Restricts result writes and completed-member reads to one run and its pinned input manifest.
    /// </summary>
    private sealed class AnalysisWriteCapability(PipelineStore store, Guid assessmentId, Guid analysisRunId) : IAnalysisResultWriter
    {
        private PinnedManifest? pinned;
        /// <summary>
        /// Pins the run input and returns its committed observation IDs for resume.
        /// </summary>
        public async Task<IReadOnlySet<Guid>> GetCompletedObservationIdsAsync(CancellationToken cancellationToken)
        {
            pinned = await store.AnalysisManifestAsync(assessmentId, analysisRunId, cancellationToken);
            return await store.CompletedResultsAsync(assessmentId, analysisRunId, cancellationToken);
        }
        /// <summary>
        /// Pins the run input when needed and commits one result after revalidating the saved observation.
        /// </summary>
        public async Task WriteAsync(AnalysisResult result, CancellationToken cancellationToken)
        {
            pinned ??= await store.AnalysisManifestAsync(assessmentId, analysisRunId, cancellationToken);
            await store.CommitResultAsync(assessmentId, analysisRunId, result, pinned, cancellationToken);
        }
    }

    /// <summary>
    /// Caches a validated manifest, its serialized digest and member lookup for consistent per-record access.
    /// </summary>
    private sealed class PinnedManifest
    {
        /// <summary>
        /// Serializes the validated manifest and builds its immutable member lookup and digest.
        /// </summary>
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
