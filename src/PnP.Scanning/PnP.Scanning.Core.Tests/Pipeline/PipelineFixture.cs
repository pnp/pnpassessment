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

internal static class FixedSource
{
    internal const string ModuleKey = "snapshot-fixture";
    internal const string InputVersion = "fixture/v1";
    internal const string Rule1 = "fixture/rule-1";
    internal const string Rule2 = "fixture/rule-2";
    internal static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "Pipeline", "Fixtures", "source-v1.json");

    internal static IReadOnlyList<CollectedObservation> Read()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path));
        return document.RootElement.GetProperty("records").EnumerateArray().Select(x => new CollectedObservation(
            Guid.Parse(x.GetProperty("id").GetString()!), x.GetProperty("identity").GetString()!,
            x.GetProperty("revision").GetString(), Enum.Parse<AcquisitionStatus>(x.GetProperty("status").GetString()!),
            VersionedJson.From(new { fixture = "source-v1", identity = x.GetProperty("identity").GetString() }),
            x.GetProperty("rawHex").GetString() is string hex ? Convert.FromHexString(hex) : null,
            x.GetProperty("error").GetString())).ToArray();
    }

    internal static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

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

    internal static ModuleRegistry Registry(FixtureControl control, bool collector = true,
        string current = Rule1, bool rule1 = true, bool rule2 = true)
    {
        var analyzers = new List<AnalysisRegistration>();
        if (rule1) analyzers.Add(new(ModuleKey, Rule1, [InputVersion], () => control.CreateAnalyzer(Rule1)));
        if (rule2) analyzers.Add(new(ModuleKey, Rule2, [InputVersion], () => control.CreateAnalyzer(Rule2)));
        return new ModuleRegistry(collector ? [new CollectionRegistration(ModuleKey, InputVersion, () => new FixtureCollector(control))] : [],
            analyzers, new Dictionary<string, string> { [ModuleKey] = current });
    }
}

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

    internal void ResetGate()
    {
        Reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    internal void Release()
    {
        BlockCollectionAt = BlockAnalysisAt = null;
        release.TrySetResult();
    }
    internal async Task WaitAsync(bool collection, Guid id, CancellationToken token)
    {
        if ((collection ? BlockCollectionAt : BlockAnalysisAt) != id) return;
        Reached.TrySetResult();
        await release.Task.WaitAsync(token);
    }
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

/// <summary>Registered only in this test assembly. It does not read a tenant or interpret ASPX.</summary>
internal sealed class FixtureCollector(FixtureControl control) : ICollectionModule
{
    public string ModuleKey => FixedSource.ModuleKey;
    public string InputVersion => FixedSource.InputVersion;
    public async IAsyncEnumerable<CollectionRecord> CollectAsync(CollectionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref control.CollectionStarts);
        control.LastCollectionOptions = context.Options.Clone();
        var records = FixedSource.Read();
        var next = context.Checkpoint?.Value.GetProperty("nextIndex").GetInt32() ?? 0;
        for (var index = next; index < records.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await control.WaitAsync(true, records[index].ObservationId, cancellationToken);
            yield return new(records[index], VersionedJson.From(new { nextIndex = index + 1 }), records.Count);
        }
    }
}

internal sealed class FixtureAnalyzer(FixtureControl control, string rule) : IAnalysisModule
{
    public string ModuleKey => FixedSource.ModuleKey;
    public string RuleVersion => rule;
    public async Task<AnalysisResult> AnalyzeAsync(SourceRecord source, VersionedJson parameters, CancellationToken cancellationToken)
    {
        control.AnalysisCalls.AddOrUpdate((rule, source.ObservationId), 1, (_, count) => count + 1);
        await control.WaitAsync(false, source.ObservationId, cancellationToken);
        var bytes = source.Artifact.GetBytes();
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

internal sealed class ForbiddenOnlineEnvironment : ICollectionEnvironment, IDataProtectionProvider
{
    internal int ProtectionRequests;
    internal int OnlineRequests;
    internal int RestoreRequests;
    internal bool ForbidRestore;

    public StartRequest Protect(StartRequest options) => options.Clone();
    public StartRequest Restore(StartRequest options)
    {
        Interlocked.Increment(ref RestoreRequests);
        if (ForbidRestore) throw new InvalidOperationException("Collection configuration must not be restored during analysis.");
        return options.Clone();
    }
    public Task<CollectionServices> OpenAsync(StartRequest options, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref OnlineRequests);
        throw new InvalidOperationException("Online collection services are forbidden in the offline fixture.");
    }
    public IDataProtector CreateProtector(string purpose)
    {
        Interlocked.Increment(ref ProtectionRequests);
        throw new InvalidOperationException("Authentication/data protection initialization is forbidden in the offline fixture.");
    }
}

internal sealed class StoreCase : IDisposable
{
    internal static string TestRoot => Environment.GetEnvironmentVariable("ASSESSMENT_TEST_ROOT") ??
        System.IO.Path.Combine(Directory.GetCurrentDirectory(), ".temp", "pipeline-tests");
    internal StoreCase(string name)
    {
        DirectoryPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(TestRoot, name, Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(DirectoryPath);
        Store = new PipelineStore(DirectoryPath);
    }
    internal string DirectoryPath { get; }
    internal PipelineStore Store { get; }

    internal async Task<(Guid Assessment, Guid Snapshot, Guid Run, ISnapshotWriter Writer)> UnsealedAsync(
        IReadOnlyList<CollectedObservation>? records = null, Guid? existingAssessment = null)
    {
        var assessment = existingAssessment ?? Guid.NewGuid();
        var snapshot = Guid.NewGuid();
        var run = Guid.NewGuid();
        var source = new SourceSnapshotRow
        {
            AssessmentId = assessment, SnapshotId = snapshot, ModuleKey = FixedSource.ModuleKey,
            InputVersion = FixedSource.InputVersion, ScopeJson = VersionedJson.From(new { scope = "fixed fixture" }).Json,
            CreatedAtUtc = DateTime.UtcNow,
        };
        var phase = new PhaseRunRow
        {
            AssessmentId = assessment, RunId = run, SnapshotId = snapshot, ModuleKey = FixedSource.ModuleKey,
            InputVersion = FixedSource.InputVersion, Kind = PhaseKind.Collection, CurrentPhase = PhaseKind.Collection,
            Status = ScanStatus.Queued, ParametersJson = VersionedJson.Empty.Json, AnalysisParametersJson = VersionedJson.Empty.Json,
            Threads = 1, CreatedAtUtc = DateTime.UtcNow,
        };
        if (existingAssessment == null)
            await Store.CreateCollectionAsync(source, phase, null, FixedSource.Request().CollectionOptions, default);
        else
        {
            using var db = Store.CreateContext(assessment);
            db.SourceSnapshots.Add(source);
            db.PhaseRuns.Add(phase);
            await db.SaveChangesAsync();
        }
        await Store.SetRunningAsync(assessment, run, run, default);
        var writer = Store.SnapshotWriter(assessment, snapshot, run);
        var rows = records ?? FixedSource.Read();
        for (int index = 0; index < rows.Count; index++)
            await writer.CommitAsync(new(rows[index], VersionedJson.From(new { nextIndex = index + 1 }), rows.Count), default);
        return (assessment, snapshot, run, writer);
    }

    internal async Task<int> ResultCountAsync(Guid assessment, Guid analysisId)
    {
        using var db = Store.CreateContext(assessment);
        return await db.AnalysisResults.CountAsync(x => x.AnalysisRunId == analysisId);
    }
    internal static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!await condition()) await Task.Delay(20, timeout.Token);
    }
    public void Dispose()
    {
        if (Environment.GetEnvironmentVariable("ASSESSMENT_TEST_KEEP_DATA") == "1") return;
        var root = System.IO.Path.GetFullPath(TestRoot).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        if (!DirectoryPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Refusing cleanup outside the test root.");
        Directory.Delete(DirectoryPath, true);
    }
}
