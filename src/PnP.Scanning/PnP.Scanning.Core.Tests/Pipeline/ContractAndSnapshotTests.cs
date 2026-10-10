#nullable enable
using System.Data.Common;
using System.Reflection;
using System.Reflection.Emit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PnP.Scanning.Core.Pipeline.Analysis;
using PnP.Scanning.Core.Pipeline.Contracts;
using PnP.Scanning.Core.Pipeline.Orchestration;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Storage.Pipeline;
using Xunit;

namespace PnP.Scanning.Core.Tests.Pipeline;

[Trait("Category", "Pipeline")]
public sealed class ContractAndSnapshotTests
{
    [Fact]
    public void Analysis_signatures_and_state_machines_have_no_online_or_mutable_storage_dependencies()
    {
        // Reused page parsers/mapping models are pure domain code. Include their IL and signatures
        // in the audit rather than allowing an uninspected namespace exception.
        bool PurePageType(Type type) => type.Namespace == "PnP.Scanning.Core.Scanners.WebPartMapping" ||
            type.Namespace == "PnP.Scanning.Core.Scanners" && new[] { "WikiContentParser", "WikiContentParseResult", "WikiWebPartPlaceholder", "HomePageDetector", "PageLayoutDetector", "SiteType" }.Contains(type.Name.Split('+')[0]) ||
            type.Namespace == "PnP.Scanning.Core.Discovery" && new[] { "DiscoveryHash", "DiscoveryGapCodes", "DiscoveryVerdict", "AspxDiscoveryIntent" }.Contains(type.Name);
        var types = typeof(AnalysisExecutor).Assembly.GetTypes().Where(x =>
            x.Namespace?.StartsWith("PnP.Scanning.Core.Pipeline.Analysis", StringComparison.Ordinal) == true ||
            x.Namespace?.StartsWith("PnP.Scanning.Core.Pipeline.Contracts", StringComparison.Ordinal) == true || PurePageType(x) || x.DeclaringType != null && PurePageType(x.DeclaringType)).ToArray();
        var referenced = types.SelectMany(x => x.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Select(f => f.FieldType).Concat(x.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType))))
            .Concat(types.SelectMany(x => x.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Cast<MethodBase>().Concat(x.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)))
                .SelectMany(IlDependencies));
        foreach (var type in referenced.SelectMany(Unwrap))
        {
            var name = type.FullName ?? "";
            Assert.False(name.StartsWith("PnP.Core.", StringComparison.Ordinal), name);
            Assert.False(name.StartsWith("Microsoft.SharePoint", StringComparison.Ordinal), name);
            Assert.False(name.StartsWith("System.Net.", StringComparison.Ordinal), name);
            Assert.False(name.StartsWith("PnP.Scanning.Core.Authentication", StringComparison.Ordinal), name);
            Assert.False(name.StartsWith("PnP.Scanning.Core.Pipeline.Collection", StringComparison.Ordinal), name);
            Assert.False(name.StartsWith("PnP.Scanning.Core.Storage", StringComparison.Ordinal), name);
            Assert.False(name.StartsWith("PnP.Scanning.Core.Services", StringComparison.Ordinal), name);
            Assert.False(name.StartsWith("Microsoft.AspNetCore.DataProtection", StringComparison.Ordinal), name);
            if (name.StartsWith("PnP.Scanning.Core.", StringComparison.Ordinal))
                Assert.True(name.StartsWith("PnP.Scanning.Core.Pipeline.Analysis", StringComparison.Ordinal) ||
                    name.StartsWith("PnP.Scanning.Core.Pipeline.Contracts", StringComparison.Ordinal) || PurePageType(type) || type.DeclaringType != null && PurePageType(type.DeclaringType), name);
        }
        Assert.DoesNotContain(typeof(ISnapshotReader).GetMethods(), x => x.Name.Contains("Write", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(IAnalysisModule).GetMethods().SelectMany(x => x.GetParameters()),
            p => p.ParameterType == typeof(ISnapshotWriter));
    }

    private static IEnumerable<Type> IlDependencies(MethodBase method)
    {
        var bytes = method.GetMethodBody()?.GetILAsByteArray();
        if (bytes == null) yield break;
        var codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(x => x.FieldType == typeof(OpCode)).Select(x => (OpCode)x.GetValue(null)!)
            .ToDictionary(x => x.Value);
        for (int offset = 0; offset < bytes.Length;)
        {
            short value = bytes[offset++];
            if (value == 0xfe) value = unchecked((short)(0xfe00 | bytes[offset++]));
            var code = codes[value];
            if (code.OperandType is OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok)
            {
                var member = method.Module.ResolveMember(BitConverter.ToInt32(bytes, offset),
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

    private static IEnumerable<Type> Unwrap(Type type)
    {
        yield return type;
        if (type.HasElementType)
            foreach (var item in Unwrap(type.GetElementType()!)) yield return item;
        foreach (var argument in type.GetGenericArguments())
            foreach (var item in Unwrap(argument)) yield return item;
    }

    [Fact]
    public async Task Offline_analysis_uses_only_read_and_result_write_capabilities_after_reopen()
    {
        using var data = new StoreCase("pipeline-offline");
        var seed = await data.UnsealedAsync();
        var info = await seed.Writer.SealAsync(default);
        var reopened = new PipelineStore(data.DirectoryPath);
        var control = new FixtureControl();
        var environment = new ForbiddenOnlineEnvironment { ForbidRestore = true };
        var coordinator = new PipelineCoordinator(reopened, FixedSource.Registry(control, collector: false), environment);
        await coordinator.StartAsync(default);
        var ticket = await coordinator.AnalyzeAsync(new AnalyzeRequest
        {
            Id = seed.Assessment.ToString(), SnapshotId = seed.Snapshot.ToString(), RuleVersion = FixedSource.Rule1,
        });
        Assert.Equal(ScanStatus.Finished, await coordinator.WaitForCompletionAsync(seed.Assessment));
        Assert.Equal(0, environment.OnlineRequests);
        Assert.Equal(0, environment.ProtectionRequests);
        Assert.Equal(0, environment.RestoreRequests);
        Assert.Equal(0, control.CollectionStarts);
        var reader = reopened.SnapshotReader(seed.Assessment, seed.Snapshot);
        Assert.False(reader is ISnapshotWriter);
        Assert.False(reopened.ResultWriter(seed.Assessment, Guid.Parse(ticket.AnalysisRunId)) is ISnapshotWriter);
        var record = await reader.ReadAsync(FixedSource.Read()[0].ObservationId, default);
        var copy = record.Artifact.GetBytes()!;
        copy[0] ^= 255;
        Assert.Equal(FixedSource.Read()[0].RawBytes, (await reader.ReadAsync(record.ObservationId, default)).Artifact.GetBytes());
        Assert.Equal(info.ManifestDigest, (await reader.OpenAsync(default)).ManifestDigest);
        using var db = reopened.CreateContext(seed.Assessment);
        Assert.Equal(6, await db.SourceObservations.CountAsync());
        Assert.Equal(6, await db.SourceArtifacts.CountAsync());
        Assert.Equal(6, await db.AnalysisResults.CountAsync());
    }

    [Fact]
    public void Production_registry_has_no_fixture_or_legacy_phase_modules()
    {
        var registry = new ModuleRegistry();
        Assert.Throws<NotSupportedException>(() => registry.GetCollector(FixedSource.ModuleKey));
        Assert.Throws<NotSupportedException>(() => registry.GetAnalyzer(FixedSource.ModuleKey, FixedSource.Rule1));
        Assert.Throws<NotSupportedException>(() => registry.GetCollector("Classic"));
    }

    [Fact]
    public async Task Sealed_snapshot_distinguishes_empty_blob_from_null_and_preserves_settled_failures()
    {
        using var data = new StoreCase("pipeline-outcomes");
        var seed = await data.UnsealedAsync();
        var sealedInfo = await seed.Writer.SealAsync(default);
        var reopened = new PipelineStore(data.DirectoryPath);
        var reader = reopened.SnapshotReader(seed.Assessment, seed.Snapshot);
        Assert.Equal(sealedInfo.ManifestDigest, (await reader.OpenAsync(default)).ManifestDigest);
        foreach (var expected in FixedSource.Read())
        {
            var actual = await reader.ReadAsync(expected.ObservationId, default);
            Assert.Equal(expected.SourceIdentity, actual.SourceIdentity);
            Assert.Equal(expected.SourceRevision, actual.SourceRevision);
            Assert.Equal(expected.AcquisitionStatus, actual.AcquisitionStatus);
            Assert.Equal(expected.AcquisitionError, actual.AcquisitionError);
            Assert.Equal(expected.RawBytes, actual.Artifact.GetBytes());
            Assert.Equal(expected.RawBytes?.LongLength, actual.Artifact.Length);
            Assert.Equal(expected.RawBytes == null ? null : FixedSource.Digest(expected.RawBytes), actual.Artifact.Sha256);
        }
        using var db = reopened.CreateContext(seed.Assessment);
        Assert.Equal(1, await db.SourceArtifacts.CountAsync(x => x.Length == 0 && x.RawBytes != null));
        Assert.Equal(3, await db.SourceArtifacts.CountAsync(x => x.RawBytes == null && x.Length == null && x.Sha256 == null));
        Assert.Equal(4, (await db.PhaseRuns.SingleAsync(x => x.RunId == seed.Run)).ErrorCount);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("reference")]
    [InlineData("owner")]
    [InlineData("length")]
    [InlineData("digest")]
    [InlineData("bytes")]
    [InlineData("schema")]
    public async Task Inconsistent_saved_storage_cannot_publish_a_seal(string fault)
    {
        using var data = new StoreCase($"pipeline-{fault}");
        var seed = await data.UnsealedAsync();
        var id = FixedSource.Read()[0].ObservationId;
        using (var db = data.Store.CreateContext(seed.Assessment))
        {
            switch (fault)
            {
                case "missing":
                    await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM SourceArtifacts WHERE ObservationId = {id}"); break;
                case "reference":
                    var other = (await db.SourceArtifacts.SingleAsync(x => x.ObservationId == FixedSource.Read()[1].ObservationId)).ArtifactId;
                    await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE SourceObservations SET ArtifactId = {other} WHERE ObservationId = {id}"); break;
                case "owner":
                    await db.Database.OpenConnectionAsync();
                    await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF");
                    await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE SourceArtifacts SET ObservationId = {Guid.NewGuid()} WHERE ObservationId = {id}"); break;
                case "length":
                    await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE SourceArtifacts SET Length = Length + 1 WHERE ObservationId = {id}"); break;
                case "digest":
                    await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE SourceArtifacts SET Sha256 = 'incorrect' WHERE ObservationId = {id}"); break;
                case "bytes":
                    await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE SourceArtifacts SET RawBytes = X'ffffffff00' WHERE ObservationId = {id}"); break;
                case "schema":
                    await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE SourceObservations SET MetadataJson = '{{\"schemaVersion\":0,\"value\":{{}}}}' WHERE ObservationId = {id}"); break;
            }
        }
        await Assert.ThrowsAsync<SnapshotIntegrityException>(() => seed.Writer.SealAsync(default));
        using var read = data.Store.CreateContext(seed.Assessment);
        var snapshot = await read.SourceSnapshots.SingleAsync();
        Assert.False(snapshot.IsSealed);
        Assert.Null(snapshot.ManifestJson);
        Assert.Null(snapshot.ManifestDigest);
        Assert.Null(snapshot.SealedAtUtc);
    }

    [Fact]
    public async Task Interrupted_transaction_rolls_back_source_blob_checkpoint_and_progress_together()
    {
        using var data = new StoreCase("pipeline-transaction");
        var seed = await data.UnsealedAsync([]);
        var interceptor = new FailArtifactCommitOnce();
        var store = new PipelineStore(data.DirectoryPath, id => new ScanContext(new DbContextOptionsBuilder<ScanContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = data.Store.DatabasePath(id), Pooling = false }.ToString())
            .AddInterceptors(interceptor).Options));
        var writer = store.SnapshotWriter(seed.Assessment, seed.Snapshot, seed.Run);
        var record = new CollectionRecord(FixedSource.Read()[0], VersionedJson.From(new { nextIndex = 1 }), 1);
        await Assert.ThrowsAsync<IOException>(() => writer.CommitAsync(record, default));
        using (var db = data.Store.CreateContext(seed.Assessment))
        {
            Assert.Equal(0, await db.SourceObservations.CountAsync());
            Assert.Equal(0, await db.SourceArtifacts.CountAsync());
            var phase = await db.PhaseRuns.SingleAsync();
            Assert.Equal(0, phase.CompletedRecords);
            Assert.Null(phase.CheckpointJson);
        }
        await writer.CommitAsync(record, default);
        await writer.SealAsync(default);
        Assert.Single((await store.OpenSnapshotAsync(seed.Assessment, seed.Snapshot)).ObservationIds);
    }

    private sealed class FailArtifactCommitOnce : DbTransactionInterceptor
    {
        private bool failed;
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!failed && eventData.Context is ScanContext context && context.SourceArtifacts.Local.Count > 0)
            {
                failed = true;
                throw new IOException("fixture interruption after SQL writes and before transaction commit");
            }
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task Sqlite_enforces_source_immutability_after_seal()
    {
        using var data = new StoreCase("pipeline-immutability");
        var seed = await data.UnsealedAsync();
        await seed.Writer.SealAsync(default);
        using var db = data.Store.CreateContext(seed.Assessment);
        foreach (var sql in new[]
        {
            "UPDATE SourceArtifacts SET RawBytes = NULL", "DELETE FROM SourceArtifacts",
            "UPDATE SourceObservations SET SourceRevision = 'replacement'", "DELETE FROM SourceObservations",
            "UPDATE SourceSnapshots SET IsSealed = 0", "DELETE FROM SourceSnapshots",
        }) await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync(sql));
        await Assert.ThrowsAsync<InvalidOperationException>(() => seed.Writer.CommitAsync(
            new(FixedSource.Read()[0], VersionedJson.Empty), default));
    }

    [Fact]
    public async Task Unsealed_and_corrupt_snapshots_are_rejected_before_an_analysis_run_is_created()
    {
        using var data = new StoreCase("pipeline-reject-input");
        var seed = await data.UnsealedAsync();
        var coordinator = new PipelineCoordinator(data.Store, FixedSource.Registry(new()), new ForbiddenOnlineEnvironment());
        var request = new AnalyzeRequest { Id = seed.Assessment.ToString(), SnapshotId = seed.Snapshot.ToString() };
        await Assert.ThrowsAsync<SnapshotIntegrityException>(() => coordinator.AnalyzeAsync(request));
        await seed.Writer.SealAsync(default);
        using (var db = data.Store.CreateContext(seed.Assessment))
        {
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER Pipeline_SourceArtifacts_sealed_UPDATE");
            await db.Database.ExecuteSqlRawAsync("UPDATE SourceArtifacts SET Length = Length + 1 WHERE RawBytes IS NOT NULL");
        }
        await Assert.ThrowsAsync<SnapshotIntegrityException>(() => coordinator.AnalyzeAsync(request));
        using var read = data.Store.CreateContext(seed.Assessment);
        Assert.Equal(0, await read.AnalysisRuns.CountAsync());
        Assert.Equal(0, await read.AnalysisResults.CountAsync());
    }
}
