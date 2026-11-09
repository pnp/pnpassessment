#nullable enable
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Pipeline.Collection.Module;
using PnP.Scanning.Core.Pipeline.Collection.Shared;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Pipeline.Orchestration;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Storage.Pipeline;

namespace PnP.Scanning.Core.Tests.Pipeline;

/// <summary>
/// Defines synthetic source identities and deterministic rule versions for pipeline tests.
/// </summary>
internal static class FixedSource
{
    internal const string ModuleKey = "snapshot-fixture";
    internal const string InputVersion = "fixture/v1";
    internal const string Rule1 = "fixture/rule-1";
    internal const string Rule2 = "fixture/rule-2";

    /// <summary>
    /// Returns synthetic native observations preserving absent, empty, partial, denied, and complete byte states.
    /// </summary>
    internal static IReadOnlyList<CollectedObservation> Read()
    {
        return Enumerable.Range(1, 6).Select(i => new CollectedObservation(
            Guid.Parse("00000000-0000-0000-0000-" + i.ToString("D12")),
            "fixture|" + i, "revision-1", i switch
            {
                1 or 2 => AcquisitionStatus.Complete, 3 => AcquisitionStatus.Partial,
                4 => AcquisitionStatus.Denied, 5 => AcquisitionStatus.NotAttempted, _ => AcquisitionStatus.Unknown,
            }, VersionedJson.From(new { fixture = "source-v1", identity = i }),
            i == 1 ? new byte[] { 0, 0xff, 13, 10 } : i == 2 ? [] : i == 3 ? new byte[] { 65 } : null,
            i > 2 ? "Synthetic acquisition unavailable" : null)).ToArray();
    }

    /// <summary>
    /// Computes the expected SHA-256 of recorded bytes.
    /// </summary>
    internal static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>
    /// Builds a fixed offline collection request with bounded concurrency.
    /// </summary>
    internal static CollectRequest Request(int threads = 1) => new()
    {
        Module = ModuleKey, ParametersJson = VersionedJson.Empty.Json,
        CollectionOptions = new StartRequest
        {
            Threads = threads, Environment = "Production", AuthMode = "Device",
            Tenant = "offline.invalid", TenantId = "11111111-1111-1111-1111-111111111111",
            ApplicationId = "22222222-2222-2222-2222-222222222222", SitesList = "https://offline.invalid/sites/fixture",
        },
    };

    /// <summary>
    /// Registers recorded collector and explicitly available deterministic rules.
    /// </summary>
    internal static ModuleRegistry Registry(FixtureControl control, bool collector = true,
        string current = Rule1, bool rule1 = true, bool rule2 = true)
    {
        List<AnalysisRegistration>? analyzers = new List<AnalysisRegistration>();
        if (rule1) analyzers.Add(new(ModuleKey, Rule1, [InputVersion], () => control.CreateAnalyzer(Rule1)));
        if (rule2) analyzers.Add(new(ModuleKey, Rule2, [InputVersion], () => control.CreateAnalyzer(Rule2)));
        return new ModuleRegistry(collector ? [new CollectionRegistration(ModuleKey, InputVersion, () => new FixtureCollector(control))] : [],
            analyzers, new Dictionary<string, string> { [ModuleKey] = current });
    }
}

/// <summary>
/// Controls recorded acquisition and analysis boundaries and counts their executions.
/// </summary>
internal sealed class FixtureControl
{
    internal int CollectionStarts;
    internal int AnalysisFactories;
    internal bool FailAnalysisFactoryOnce;
    internal StartRequest? LastCollectionOptions;
    internal ConcurrentDictionary<(string Rule, Guid Id), int> AnalysisCalls { get; } = new();
    internal Guid? BlockCollectionAt;
    internal Guid? BlockAnalysisAt;
    internal TaskCompletionSource Reached { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Resets synchronization gates for the next controlled phase boundary.
    /// </summary>
    internal void ResetGate()
    {
        Reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    /// <summary>
    /// Releases blocked recorded work and clears its configured boundary.
    /// </summary>
    internal void Release()
    {
        BlockCollectionAt = BlockAnalysisAt = null;
        release.TrySetResult();
    }
    /// <summary>
    /// Blocks only the selected acquisition or analysis identity until released or canceled.
    /// </summary>
    internal async Task WaitAsync(bool collection, Guid id, CancellationToken token)
    {
        if ((collection ? BlockCollectionAt : BlockAnalysisAt) != id) return;
        Reached.TrySetResult();
        await release.Task.WaitAsync(token);
    }
    /// <summary>
    /// Creates a deterministic recorded rule and optionally injects one construction failure.
    /// </summary>
    internal IAnalysisModule CreateAnalyzer(string rule)
    {
        Interlocked.Increment(ref AnalysisFactories);
        if (FailAnalysisFactoryOnce)
        {
            FailAnalysisFactoryOnce = false;
            throw new IOException("fixture analysis host unavailable after collection sealed");
        }
        return new FixtureAnalyzer(this, rule);
    }
}

/// <summary>
/// Emits recorded source bytes beginning at the durable collection checkpoint.
/// </summary>
internal sealed class FixtureCollector(FixtureControl control) : ICollectionModule
{
    public string ModuleKey => FixedSource.ModuleKey;
    public string InputVersion => FixedSource.InputVersion;
    /// <summary>
    /// Resumes the recorded source sequence from its saved checkpoint and yields remaining receipts.
    /// </summary>
    public async IAsyncEnumerable<CollectionRecord> CollectAsync(CollectionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref control.CollectionStarts);
        control.LastCollectionOptions = context.Options.Clone();
        IReadOnlyList<CollectedObservation>? records = FixedSource.Read();
        int next = context.Checkpoint?.Value.GetProperty("nextIndex").GetInt32() ?? 0;
        for (int index = next; index < records.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await control.WaitAsync(true, records[index].ObservationId, cancellationToken);
            yield return new(records[index], VersionedJson.From(new { nextIndex = index + 1 }), records.Count);
        }
    }
}

/// <summary>
/// Evaluates recorded bytes deterministically without authentication or online access.
/// </summary>
internal sealed class FixtureAnalyzer(FixtureControl control, string rule) : IAnalysisModule
{
    public string ModuleKey => FixedSource.ModuleKey;
    public string RuleVersion => rule;
    /// <summary>
    /// Returns a deterministic payload from saved bytes, revision, rule, and fixed parameters.
    /// </summary>
    public async Task<AnalysisResult> AnalyzeAsync(SourceRecord source, VersionedJson parameters, CancellationToken cancellationToken)
    {
        control.AnalysisCalls.AddOrUpdate((rule, source.ObservationId), 1, (_, count) => count + 1);
        await control.WaitAsync(false, source.ObservationId, cancellationToken);
        byte[]? bytes = source.Artifact.GetBytes();
        return new(source.ObservationId,
            source.AcquisitionStatus == AcquisitionStatus.Complete ? AnalysisOutcome.Analyzed : AnalysisOutcome.Unknown,
            source.AcquisitionStatus == AcquisitionStatus.Complete ? $"{rule}: fixture content acquired" : $"{rule}: {source.AcquisitionStatus}",
            VersionedJson.From(new
            {
                rule, source.SourceIdentity, source.SourceRevision,
                rawHex = bytes == null ? null : Convert.ToHexString(bytes).ToLowerInvariant(),
                length = bytes?.LongLength, digest = bytes == null ? null : FixedSource.Digest(bytes),
                parameters = parameters.Value,
            }));
    }
}

/// <summary>
/// Rejects online service resolution and optionally forbids collection configuration restoration.
/// </summary>
internal sealed class ForbiddenOnlineEnvironment : ICollectionEnvironment, IDataProtectionProvider
{
    internal int ProtectionRequests;
    internal int OnlineRequests;
    internal int RestoreRequests;
    internal bool ForbidRestore;

    /// <summary>
    /// Clones synthetic options without constructing authentication services.
    /// </summary>
    public StartRequest Protect(StartRequest options) => options.Clone();
    /// <summary>
    /// Counts configuration restoration and rejects it when detached analysis forbids collection resume.
    /// </summary>
    public StartRequest Restore(StartRequest options)
    {
        Interlocked.Increment(ref RestoreRequests);
        if (ForbidRestore) throw new InvalidOperationException("Collection configuration must not be restored during analysis.");
        return options.Clone();
    }
    /// <summary>
    /// Counts and rejects online collection service resolution in offline tests.
    /// </summary>
    public Task<CollectionServices> OpenAsync(StartRequest options, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref OnlineRequests);
        throw new InvalidOperationException("Online collection services are forbidden in the offline fixture.");
    }
    /// <summary>
    /// Counts and rejects authentication-related protection-provider construction.
    /// </summary>
    public IDataProtector CreateProtector(string purpose)
    {
        Interlocked.Increment(ref ProtectionRequests);
        throw new InvalidOperationException("Authentication/data protection initialization is forbidden in the offline fixture.");
    }
}

/// <summary>
/// Owns a uniquely scoped SQLite store and its bounded test-directory cleanup.
/// </summary>
internal sealed class StoreCase : IDisposable
{
    internal static string TestRoot => Environment.GetEnvironmentVariable("ASSESSMENT_TEST_ROOT") ??
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "assessment-pipeline-tests");
    /// <summary>
    /// Initializes Store Case with the supplied dependencies and fixed scope.
    /// </summary>
    internal StoreCase(string name)
    {
        DirectoryPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(TestRoot, name, Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(DirectoryPath);
        Store = new PipelineStore(DirectoryPath);
    }
    internal string DirectoryPath { get; }
    internal PipelineStore Store { get; }

    /// <summary>
    /// Creates a running synthetic snapshot and atomically commits the selected recorded observations.
    /// </summary>
    internal async Task<(Guid Assessment, Guid Snapshot, Guid Run, ISnapshotWriter Writer)> UnsealedAsync(
        IReadOnlyList<CollectedObservation>? records = null, Guid? existingAssessment = null)
    {
        Guid assessment = existingAssessment ?? Guid.NewGuid();
        Guid snapshot = Guid.NewGuid();
        Guid run = Guid.NewGuid();
        SourceSnapshotRow? source = new SourceSnapshotRow
        {
            AssessmentId = assessment, SnapshotId = snapshot, ModuleKey = FixedSource.ModuleKey,
            InputVersion = FixedSource.InputVersion, ScopeJson = VersionedJson.From(new { scope = "fixed fixture" }).Json,
            CreatedAtUtc = DateTime.UtcNow,
        };
        PhaseRunRow? phase = new PhaseRunRow
        {
            AssessmentId = assessment, RunId = run, SnapshotId = snapshot, ModuleKey = FixedSource.ModuleKey,
            InputVersion = FixedSource.InputVersion, Kind = PhaseKind.Collection, CurrentPhase = PhaseKind.Collection,
            Status = ScanStatus.Queued, ParametersJson = new PipelineRunSettings(1, VersionedJson.Empty.Json, VersionedJson.Empty.Json).Serialize(), CreatedAtUtc = DateTime.UtcNow,
        };
        if (existingAssessment == null)
            await Store.CreateCollectionAsync(new PnP.Scanning.Core.Storage.Scan { ScanId = assessment, CLIMode = "Pipeline:" + FixedSource.ModuleKey, Status = ScanStatus.Queued }, source, phase, null, default);
        else
        {
            using ScanContext? db = Store.CreateContext(assessment);
            db.SourceSnapshots.Add(source);
            db.PhaseRuns.Add(phase);
            await db.SaveChangesAsync();
        }
        await Store.SetRunningAsync(assessment, run, run, default);
        ISnapshotWriter? writer = Store.SnapshotWriter(assessment, snapshot, run);
        IReadOnlyList<CollectedObservation>? rows = records ?? FixedSource.Read();
        for (int index = 0; index < rows.Count; index++)
            await writer.CommitAsync(new(rows[index], VersionedJson.From(new { nextIndex = index + 1 }), rows.Count), default);
        return (assessment, snapshot, run, writer);
    }

    /// <summary>
    /// Counts committed results for the selected recorded analysis run.
    /// </summary>
    internal async Task<int> ResultCountAsync(Guid assessment, Guid analysisId)
    {
        using ScanContext? db = Store.CreateContext(assessment);
        return await db.AnalysisResults.CountAsync(x => x.AnalysisRunId == analysisId);
    }
    /// <summary>
    /// Waits for a recorded asynchronous lifecycle condition with a bounded timeout.
    /// </summary>
    internal static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        using CancellationTokenSource? timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!await condition()) await Task.Delay(20, timeout.Token);
    }
    /// <summary>
    /// Removes only this fixture's uniquely resolved test directory unless retention was requested.
    /// </summary>
    public void Dispose()
    {
        if (Environment.GetEnvironmentVariable("ASSESSMENT_TEST_KEEP_DATA") == "1") return;
        string? root = System.IO.Path.GetFullPath(TestRoot).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        if (!DirectoryPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Refusing cleanup outside the test root.");
        Directory.Delete(DirectoryPath, true);
    }
}
