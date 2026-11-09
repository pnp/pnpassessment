#nullable enable
using System.Data.Common;
using System.Reflection;
using System.Reflection.Emit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PnP.Scanning.Core.Pipeline.Analysis.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Pipeline.Orchestration;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Storage.Pipeline;
using Xunit;

namespace PnP.Scanning.Core.Tests.Pipeline;

/// <summary>
/// Verifies source contracts, bound capabilities, transaction atomicity, and snapshot integrity.
/// </summary>
[Trait("Category", "Pipeline")]
public sealed class ContractAndSnapshotTests
{
    /// <summary>
    /// Verifies that foundation analysis parameters preserve non object values and unrelated runtime properties.
    /// </summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("\"source-rule-parameters\"")]
    [InlineData("{\"runtime\":{\"custom\":true}}")]
    public async Task Foundation_analysis_parameters_preserve_non_object_values_and_unrelated_runtime_properties(string json)
    {
        using StoreCase? data = new StoreCase("pipeline-parameter-compatibility");
        (Guid Assessment, Guid Snapshot, Guid Run, ISnapshotWriter Writer) seed = await data.UnsealedAsync();
        await seed.Writer.SealAsync(default);
        using System.Text.Json.JsonDocument? document = System.Text.Json.JsonDocument.Parse(json);
        VersionedJson? parameters = VersionedJson.From(document.RootElement);
        Guid runId = Guid.NewGuid();
        await data.Store.CreateAnalysisAsync(new PhaseRunRow
        {
            AssessmentId = seed.Assessment, SnapshotId = seed.Snapshot, RunId = runId,
            Kind = PhaseKind.Analysis, CurrentPhase = PhaseKind.Analysis, Status = ScanStatus.Queued,
            ModuleKey = FixedSource.ModuleKey, InputVersion = FixedSource.InputVersion,
            RuleVersion = FixedSource.Rule1, ParametersJson = parameters.Json, CreatedAtUtc = DateTime.UtcNow,
        });
        await data.Store.SetRunningAsync(seed.Assessment, runId);
        Assert.Empty(await data.Store.ResultWriter(seed.Assessment, runId).GetCompletedObservationIdsAsync(default));
        using ScanContext? db = data.Store.CreateContext(seed.Assessment);
        Assert.Equal(parameters.Json, (await db.AnalysisRuns.SingleAsync(x => x.AnalysisRunId == runId)).ParametersJson);
    }

    /// <summary>
    /// Verifies that analysis entrypoints and state machines have no online or mutable storage dependencies.
    /// </summary>
    [Fact]
    public void Analysis_entrypoints_and_state_machines_have_no_online_or_mutable_storage_dependencies()
    {
        bool PurePageType(Type type) => type.Namespace == "PnP.Scanning.Core.Discovery" &&
            new[] { "DiscoveryHash", "DiscoveryGapCodes", "DiscoveryVerdict", "AspxDiscoveryIntent" }.Contains(type.Name);
        Type[]? types = typeof(AnalysisExecutor).Assembly.GetTypes().Where(x =>
            x.Namespace == "PnP.Scanning.Core.Pipeline.Analysis.Module" && x.Name != "ClassicPageInputIndex" ||
            x.Namespace?.StartsWith("PnP.Scanning.Core.Pipeline.Contracts", StringComparison.Ordinal) == true || PurePageType(x) || x.DeclaringType != null && PurePageType(x.DeclaringType)).ToArray();
        IEnumerable<Type>? referenced = types.SelectMany(x => x.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Select(f => f.FieldType).Concat(x.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType))))
            .Concat(types.SelectMany(x => x.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Cast<MethodBase>().Concat(x.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)))
                .SelectMany(IlDependencies));
        foreach (Type? type in referenced.SelectMany(Unwrap))
        {
            string? name = type.FullName ?? "";
            Assert.False(name.StartsWith("PnP.Core.", StringComparison.Ordinal), name);
            Assert.False(name.StartsWith("Microsoft.SharePoint", StringComparison.Ordinal), name);
            Assert.False(name.StartsWith("System.Net.", StringComparison.Ordinal), name);
            Assert.False(name.StartsWith("PnP.Scanning.Core.Authentication", StringComparison.Ordinal), name);
            Assert.False(name.StartsWith("PnP.Scanning.Core.Pipeline.Collection", StringComparison.Ordinal), name);
            bool compatibilityProjection = name == "PnP.Scanning.Core.Storage.Pipeline.ClassicCompatibility";
            Assert.False(name.StartsWith("PnP.Scanning.Core.Storage", StringComparison.Ordinal) && !compatibilityProjection, name);
            Assert.False(name.StartsWith("PnP.Scanning.Core.Services", StringComparison.Ordinal), name);
            bool pureMapping = name == "PnP.Scanning.Core.Scanners.WebPartMapping.WebPartMappingManager";
            Assert.False(name.StartsWith("PnP.Scanning.Core.Scanners", StringComparison.Ordinal) && !pureMapping, name);
            Assert.False(name.StartsWith("Microsoft.AspNetCore.DataProtection", StringComparison.Ordinal), name);
            if (name.StartsWith("PnP.Scanning.Core.", StringComparison.Ordinal))
                Assert.True(name.StartsWith("PnP.Scanning.Core.Pipeline.Analysis", StringComparison.Ordinal) ||
                    name.StartsWith("PnP.Scanning.Core.Pipeline.Contracts", StringComparison.Ordinal) || pureMapping || compatibilityProjection ||
                    PurePageType(type) || type.DeclaringType != null && PurePageType(type.DeclaringType), name);
        }
        Assert.DoesNotContain(typeof(ISnapshotReader).GetMethods(), x => x.Name.Contains("Write", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(IAnalysisModule).GetMethods().SelectMany(x => x.GetParameters()),
            p => p.ParameterType == typeof(ISnapshotWriter));
    }

    /// <summary>
    /// Enumerates statically referenced types from IL instructions for the offline-boundary assertion.
    /// </summary>
    private static IEnumerable<Type> IlDependencies(MethodBase method)
    {
        byte[]? bytes = method.GetMethodBody()?.GetILAsByteArray();
        if (bytes == null) yield break;
        Dictionary<short, OpCode>? codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(x => x.FieldType == typeof(OpCode)).Select(x => (OpCode)x.GetValue(null)!)
            .ToDictionary(x => x.Value);
        for (int offset = 0; offset < bytes.Length;)
        {
            short value = bytes[offset++];
            if (value == 0xfe) value = unchecked((short)(0xfe00 | bytes[offset++]));
            OpCode code = codes[value];
            if (code.OperandType is OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok)
            {
                MemberInfo? member = method.Module.ResolveMember(BitConverter.ToInt32(bytes, offset),
                    method.DeclaringType?.GetGenericArguments(), method.IsGenericMethod ? method.GetGenericArguments() : null);
                if (member is Type type) yield return type;
                else if (member?.DeclaringType is Type owner) yield return owner;
            }
            offset += code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(bytes, offset),
                _ => 4,
            };
        }
    }

    /// <summary>
    /// Expands array and generic type arguments for dependency-boundary assertions.
    /// </summary>
    private static IEnumerable<Type> Unwrap(Type type)
    {
        yield return type;
        if (type.HasElementType)
            foreach (Type? item in Unwrap(type.GetElementType()!)) yield return item;
        foreach (Type? argument in type.GetGenericArguments())
            foreach (Type? item in Unwrap(argument)) yield return item;
    }

    /// <summary>
    /// Verifies that offline analysis uses only read and result write capabilities after reopen.
    /// </summary>
    [Fact]
    public async Task Offline_analysis_uses_only_read_and_result_write_capabilities_after_reopen()
    {
        using StoreCase? data = new StoreCase("pipeline-offline");
        (Guid Assessment, Guid Snapshot, Guid Run, ISnapshotWriter Writer) seed = await data.UnsealedAsync();
        SnapshotInfo? info = await seed.Writer.SealAsync(default);
        PipelineStore? reopened = new PipelineStore(data.DirectoryPath);
        FixtureControl? control = new FixtureControl();
        ForbiddenOnlineEnvironment? environment = new ForbiddenOnlineEnvironment { ForbidRestore = true };
        PipelineCoordinator? coordinator = new PipelineCoordinator(reopened, FixedSource.Registry(control, collector: false), environment);
        await coordinator.StartAsync(default);
        PhaseReply? ticket = await coordinator.AnalyzeAsync(new AnalyzeRequest
        {
            Id = seed.Assessment.ToString(), SnapshotId = seed.Snapshot.ToString(), RuleVersion = FixedSource.Rule1,
        });
        Assert.Equal(ScanStatus.Finished, await coordinator.WaitForCompletionAsync(seed.Assessment));
        Assert.Equal(0, environment.OnlineRequests);
        Assert.Equal(0, environment.ProtectionRequests);
        Assert.Equal(0, environment.RestoreRequests);
        Assert.Equal(0, control.CollectionStarts);
        ISnapshotReader? reader = reopened.SnapshotReader(seed.Assessment, seed.Snapshot);
        Assert.False(reader is ISnapshotWriter);
        Assert.False(reopened.ResultWriter(seed.Assessment, Guid.Parse(ticket.AnalysisRunId)) is ISnapshotWriter);
        SourceRecord? record = await reader.ReadAsync(FixedSource.Read()[0].ObservationId, default);
        byte[]? copy = record.Artifact.GetBytes()!;
        copy[0] ^= 255;
        Assert.Equal(FixedSource.Read()[0].RawBytes, (await reader.ReadAsync(record.ObservationId, default)).Artifact.GetBytes());
        Assert.Equal(info.ManifestDigest, (await reader.OpenAsync(default)).ManifestDigest);
        using ScanContext? db = reopened.CreateContext(seed.Assessment);
        Assert.Equal(6, await db.SourceObservations.CountAsync());
        Assert.Equal(6, await db.AnalysisResults.CountAsync());
    }

    /// <summary>
    /// Verifies that production registry has no fixture or legacy phase modules.
    /// </summary>
    [Fact]
    public void Production_registry_has_no_fixture_or_legacy_phase_modules()
    {
        ModuleRegistry? registry = new ModuleRegistry();
        Assert.Throws<NotSupportedException>(() => registry.GetCollector(FixedSource.ModuleKey));
        Assert.Throws<NotSupportedException>(() => registry.GetAnalyzer(FixedSource.ModuleKey, FixedSource.Rule1));
        Assert.Throws<NotSupportedException>(() => registry.GetCollector("Classic"));
    }

    /// <summary>
    /// Verifies that sealed snapshot distinguishes empty blob from null and preserves settled failures.
    /// </summary>
    [Fact]
    public async Task Sealed_snapshot_distinguishes_empty_blob_from_null_and_preserves_settled_failures()
    {
        using StoreCase? data = new StoreCase("pipeline-outcomes");
        (Guid Assessment, Guid Snapshot, Guid Run, ISnapshotWriter Writer) seed = await data.UnsealedAsync();
        SnapshotInfo? sealedInfo = await seed.Writer.SealAsync(default);
        PipelineStore? reopened = new PipelineStore(data.DirectoryPath);
        ISnapshotReader? reader = reopened.SnapshotReader(seed.Assessment, seed.Snapshot);
        Assert.Equal(sealedInfo.ManifestDigest, (await reader.OpenAsync(default)).ManifestDigest);
        foreach (CollectedObservation? expected in FixedSource.Read())
        {
            SourceRecord? actual = await reader.ReadAsync(expected.ObservationId, default);
            Assert.Equal(expected.SourceIdentity, actual.SourceIdentity);
            Assert.Equal(expected.SourceRevision, actual.SourceRevision);
            Assert.Equal(expected.AcquisitionStatus, actual.AcquisitionStatus);
            Assert.Equal(expected.AcquisitionError, actual.AcquisitionError);
            Assert.Equal(expected.RawBytes, actual.Artifact.GetBytes());
            Assert.Equal(expected.RawBytes?.LongLength, actual.Artifact.Length);
            Assert.Equal(expected.RawBytes == null ? null : FixedSource.Digest(expected.RawBytes), actual.Artifact.Sha256);
        }
        using ScanContext? db = reopened.CreateContext(seed.Assessment);
        Assert.Equal(1, await db.SourceObservations.CountAsync(x => x.Length == 0 && x.RawBytes != null));
        Assert.Equal(3, await db.SourceObservations.CountAsync(x => x.RawBytes == null && x.Length == null && x.Sha256 == null));
        Assert.Equal(4, (await db.PhaseRuns.SingleAsync(x => x.RunId == seed.Run)).ErrorCount);
    }

    /// <summary>
    /// Verifies that inconsistent saved storage cannot publish a seal.
    /// </summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("null-content")]
    [InlineData("owner")]
    [InlineData("length")]
    [InlineData("digest")]
    [InlineData("bytes")]
    [InlineData("schema")]
    public async Task Inconsistent_saved_storage_cannot_publish_a_seal(string fault)
    {
        using StoreCase? data = new StoreCase($"pipeline-{fault}");
        (Guid Assessment, Guid Snapshot, Guid Run, ISnapshotWriter Writer) seed = await data.UnsealedAsync();
        Guid id = FixedSource.Read()[0].ObservationId;
        using (ScanContext? db = data.Store.CreateContext(seed.Assessment))
        {
            switch (fault)
            {
                case "missing":
                    await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM SourceObservations WHERE ObservationId = {id}"); break;
                case "null-content":
                    await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE SourceObservations SET RawBytes = NULL WHERE ObservationId = {id}"); break;
                case "owner":
                    await db.Database.OpenConnectionAsync();
                    await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF");
                    await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE SourceObservations SET SnapshotId = {Guid.NewGuid()} WHERE ObservationId = {id}"); break;
                case "length":
                    await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE SourceObservations SET Length = Length + 1 WHERE ObservationId = {id}"); break;
                case "digest":
                    await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE SourceObservations SET Sha256 = 'incorrect' WHERE ObservationId = {id}"); break;
                case "bytes":
                    await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE SourceObservations SET RawBytes = X'ffffffff00' WHERE ObservationId = {id}"); break;
                case "schema":
                    await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE SourceObservations SET MetadataJson = '{{\"schemaVersion\":0,\"value\":{{}}}}' WHERE ObservationId = {id}"); break;
            }
        }
        await Assert.ThrowsAsync<SnapshotIntegrityException>(() => seed.Writer.SealAsync(default));
        using ScanContext? read = data.Store.CreateContext(seed.Assessment);
        SourceSnapshotRow? snapshot = await read.SourceSnapshots.SingleAsync();
        Assert.False(snapshot.IsSealed);
        Assert.Null(snapshot.ManifestJson);
        Assert.Null(snapshot.ManifestDigest);
        Assert.Null(snapshot.SealedAtUtc);
    }

    /// <summary>
    /// Verifies that interrupted transaction rolls back source blob checkpoint and progress together.
    /// </summary>
    [Fact]
    public async Task Interrupted_transaction_rolls_back_source_blob_checkpoint_and_progress_together()
    {
        using StoreCase? data = new StoreCase("pipeline-transaction");
        (Guid Assessment, Guid Snapshot, Guid Run, ISnapshotWriter Writer) seed = await data.UnsealedAsync([]);
        FailSourceCommitOnce? interceptor = new FailSourceCommitOnce();
        PipelineStore? store = new PipelineStore(data.DirectoryPath, id => new ScanContext(new DbContextOptionsBuilder<ScanContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = data.Store.DatabasePath(id), Pooling = false }.ToString())
            .AddInterceptors(interceptor).Options));
        ISnapshotWriter? writer = store.SnapshotWriter(seed.Assessment, seed.Snapshot, seed.Run);
        CollectionRecord? record = new CollectionRecord(FixedSource.Read()[0], VersionedJson.From(new { nextIndex = 1 }), 1);
        await Assert.ThrowsAsync<IOException>(() => writer.CommitAsync(record, default));
        using (ScanContext? db = data.Store.CreateContext(seed.Assessment))
        {
            Assert.Equal(0, await db.SourceObservations.CountAsync());
            PhaseRunRow? phase = await db.PhaseRuns.SingleAsync();
            Assert.Equal(0, phase.CompletedRecords);
            Assert.Null(phase.CheckpointJson);
        }
        await writer.CommitAsync(record, default);
        await writer.SealAsync(default);
        Assert.Single((await store.OpenSnapshotAsync(seed.Assessment, seed.Snapshot)).ObservationIds);
    }

    /// <summary>
    /// Injects one failure before a source transaction commits to verify atomic rollback.
    /// </summary>
    private sealed class FailSourceCommitOnce : DbTransactionInterceptor
    {
        private bool failed;
        /// <summary>
        /// Injects the configured storage failure before durable commit for atomicity assertions.
        /// </summary>
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!failed && eventData.Context is ScanContext context && context.SourceObservations.Local.Count > 0)
            {
                failed = true;
                throw new IOException("fixture interruption after SQL writes and before transaction commit");
            }
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>
    /// Verifies that snapshot capabilities enforce source immutability after seal.
    /// </summary>
    [Fact]
    public async Task Snapshot_capabilities_enforce_source_immutability_after_seal()
    {
        using StoreCase? data = new StoreCase("pipeline-immutability");
        (Guid Assessment, Guid Snapshot, Guid Run, ISnapshotWriter Writer) seed = await data.UnsealedAsync();
        await seed.Writer.SealAsync(default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => seed.Writer.CommitAsync(
            new(FixedSource.Read()[0], VersionedJson.Empty), default));
        SnapshotInfo? before = await data.Store.OpenSnapshotAsync(seed.Assessment, seed.Snapshot);
        SnapshotInfo? after = await seed.Writer.SealAsync(default);
        Assert.Equal(before.ManifestDigest, after.ManifestDigest);
    }

    /// <summary>
    /// Verifies that unsealed and corrupt snapshots are rejected before an analysis run is created.
    /// </summary>
    [Fact]
    public async Task Unsealed_and_corrupt_snapshots_are_rejected_before_an_analysis_run_is_created()
    {
        using StoreCase? data = new StoreCase("pipeline-reject-input");
        (Guid Assessment, Guid Snapshot, Guid Run, ISnapshotWriter Writer) seed = await data.UnsealedAsync();
        PipelineCoordinator? coordinator = new PipelineCoordinator(data.Store, FixedSource.Registry(new()), new ForbiddenOnlineEnvironment());
        AnalyzeRequest? request = new AnalyzeRequest { Id = seed.Assessment.ToString(), SnapshotId = seed.Snapshot.ToString() };
        await Assert.ThrowsAsync<SnapshotIntegrityException>(() => coordinator.AnalyzeAsync(request));
        await seed.Writer.SealAsync(default);
        using (ScanContext? db = data.Store.CreateContext(seed.Assessment))
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE SourceObservations SET Length = Length + 1 WHERE RawBytes IS NOT NULL");
        }
        await Assert.ThrowsAsync<SnapshotIntegrityException>(() => coordinator.AnalyzeAsync(request));
        using ScanContext? read = data.Store.CreateContext(seed.Assessment);
        Assert.Equal(0, await read.AnalysisRuns.CountAsync());
        Assert.Equal(0, await read.AnalysisResults.CountAsync());
    }
}
