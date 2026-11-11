#nullable enable
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PnP.Scanning.Core.ClassicPages;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Pipeline.Orchestration;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Storage.Pipeline;
using Xunit;

namespace PnP.Scanning.Core.Tests.Pipeline;

/// <summary>Bounds materialization reads while preserving sealed evidence and atomic compatibility output.</summary>
public sealed class ClassicMaterializationPerformanceTests
{
    /// <summary>Reads the seal once for audit restoration and reloads only audit artifacts after full validation.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Materialization_reads_only_selected_audit_artifacts_and_preserves_every_report_row(bool usage)
    {
        using StoreCase data = new("classic-materialization-reads");
        (Guid assessment, Guid run) = await CompleteAsync(data, usage);
        string[] before = await ReportRowsAsync(data.Store, assessment);
        Assert.NotEmpty(before);
        int members;
        int chunks;
        using (ScanContext db = data.Store.CreateContext(assessment, readOnly: true))
        {
            string[] metadata = await db.SourceObservations.Select(row => row.MetadataJson).ToArrayAsync();
            members = metadata.Length;
            chunks = metadata.Count(json => new VersionedJson(json).Value.GetProperty("kind").GetString() == "AuditChunk");
            if (usage)
                Assert.Contains(await db.ClassicPageAuditUsages.ToListAsync(), row => row.QueryStatus == "partial");
        }

        Assert.True(members > chunks);
        SourceReads reads = new();
        PipelineStore store = Instrumented(data, reads);
        await store.MaterializeClassicAsync(assessment, run, default);
        Assert.Equal(usage ? 2 : 1, reads.SnapshotReads);
        Assert.Equal(1, reads.FullObservationReads);
        Assert.Equal(usage ? chunks : 0, reads.SelectedObservationReads);
        Assert.Equal(before, await ReportRowsAsync(store, assessment));
    }

    /// <summary>Unselected source content, metadata, membership and publication tampering still reject the complete transaction.</summary>
    [Theory]
    [InlineData("content")]
    [InlineData("digest")]
    [InlineData("metadata")]
    [InlineData("membership")]
    [InlineData("identity")]
    [InlineData("seal")]
    [InlineData("publication")]
    public async Task Materialization_rejects_tampering_without_replacing_report_rows(string mutation)
    {
        using StoreCase data = new("classic-materialization-integrity");
        (Guid assessment, Guid run) = await CompleteAsync(data, true);
        string[] before = await ReportRowsAsync(data.Store, assessment);
        using (ScanContext db = data.Store.CreateContext(assessment))
        {
            if (mutation == "publication")
                (await db.AnalysisReportRows.SingleAsync()).PayloadJson = VersionedJson.Empty.Json;
            else if (mutation == "seal")
                (await db.SourceSnapshots.SingleAsync()).ManifestDigest = new string('0', 64);
            else
            {
                SourceObservationRow row = (await db.SourceObservations.ToListAsync())
                    .First(row => new VersionedJson(row.MetadataJson).Value.GetProperty("kind").GetString() != "AuditChunk");
                if (mutation == "content") row.RawBytes = [1, 2, 3];
                if (mutation == "digest") row.Sha256 = new string('0', 64);
                if (mutation == "metadata") row.MetadataJson = VersionedJson.From(new { kind = "AuditChunk" }).Json;
                if (mutation == "identity") row.SourceIdentity = "Foreign:synthetic";
                if (mutation == "membership")
                    db.SourceObservations.Add(new SourceObservationRow
                    {
                        ObservationId = Guid.NewGuid(),
                        SnapshotId = row.SnapshotId,
                        SourceIdentity = "Unexpected:synthetic",
                        SourceRevision = row.SourceRevision,
                        AcquisitionStatus = row.AcquisitionStatus,
                        MetadataJson = row.MetadataJson,
                        AcquisitionError = row.AcquisitionError,
                        RawBytes = row.RawBytes,
                        Length = row.Length,
                        Sha256 = row.Sha256,
                    });
            }
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<SnapshotIntegrityException>(() =>
            data.Store.MaterializeClassicAsync(assessment, run, default));
        Assert.Equal(before, await ReportRowsAsync(data.Store, assessment));
    }

    /// <summary>Cancellation and storage failure during selected audit restoration roll back preceding deletes and inserts.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Audit_restoration_failure_rolls_back_and_finished_publication_can_be_retried(bool cancel)
    {
        using StoreCase data = new("classic-materialization-atomicity");
        (Guid assessment, Guid run) = await CompleteAsync(data, true);
        string[] before = await ReportRowsAsync(data.Store, assessment);
        using CancellationTokenSource cancellation = new();
        SourceReads reads = new()
        {
            BeforeSelectedRead = () =>
            {
                if (cancel)
                {
                    cancellation.Cancel();
                    cancellation.Token.ThrowIfCancellationRequested();
                }
                throw new IOException("Synthetic audit restoration read failure.");
            },
        };
        PipelineStore store = Instrumented(data, reads);
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.MaterializeClassicAsync(assessment, run, cancellation.Token));
        else
            await Assert.ThrowsAsync<IOException>(() => store.MaterializeClassicAsync(assessment, run, default));
        Assert.Equal(1, reads.SelectedObservationReads);
        Assert.Equal(before, await ReportRowsAsync(data.Store, assessment));
        await data.Store.MaterializeClassicAsync(assessment, run, default);
        Assert.Equal(before, await ReportRowsAsync(data.Store, assessment));
        using ScanContext db = data.Store.CreateContext(assessment, readOnly: true);
        Assert.Equal(ScanStatus.Finished, (await db.PhaseRuns.SingleAsync(row => row.RunId == run)).Status);
        Assert.Single(await db.AnalysisRuns.ToListAsync());
    }

    /// <summary>Runs synthetic native collection and analysis once, then reuses its finished publication.</summary>
    private static async Task<(Guid Assessment, Guid Run)> CompleteAsync(StoreCase data, bool usage)
    {
        ClassicContentFixture source = new() { IncludeBlog = true, IncludeSecondBlog = true, PartialAudit = true };
        PipelineCoordinator coordinator = new(
            data.Store, ClassicPageModule.Registry(source.OpenAsync), new ForbiddenOnlineEnvironment());
        Core.Services.PhaseReply ticket = await coordinator.StartPipelineAsync(new()
        {
            Collection = ClassicContentFixture.Request(usage),
        });
        Guid assessment = Guid.Parse(ticket.AssessmentId);
        Assert.Equal(ScanStatus.Finished, await coordinator.WaitForCompletionAsync(assessment));
        using ScanContext db = data.Store.CreateContext(assessment, readOnly: true);
        Guid run = (await db.PhaseRuns.SingleAsync(row => row.Kind == PhaseKind.Analysis)).RunId;
        return (assessment, run);
    }

    /// <summary>Attaches read instrumentation only to the materialization write context.</summary>
    private static PipelineStore Instrumented(StoreCase data, SourceReads reads) =>
        new(data.DirectoryPath, id => new ScanContext(new DbContextOptionsBuilder<ScanContext>()
            .UseSqlite($"Data Source={data.Store.DatabasePath(id)};Pooling=False")
            .AddInterceptors(reads).Options));

    /// <summary>Captures every persisted compatibility row including physical identities, audit coverage and rollups.</summary>
    private static async Task<string[]> ReportRowsAsync(PipelineStore store, Guid assessment)
    {
        using ScanContext db = store.CreateContext(assessment, readOnly: true);
        await db.Database.OpenConnectionAsync();
        List<string> rows = [];
        foreach (string table in db.Model.GetEntityTypes()
                     .Where(entity => entity.FindProperty("ScanId") != null)
                     .Select(entity => entity.GetTableName()!).Distinct().Order())
        {
            using DbCommand command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = $"SELECT * FROM \"{table}\" WHERE \"ScanId\" = $scan";
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = "$scan";
            parameter.Value = assessment.ToString().ToUpperInvariant();
            command.Parameters.Add(parameter);
            using DbDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                object[] values = new object[reader.FieldCount];
                reader.GetValues(values);
                rows.Add(table + ":" + JsonSerializer.Serialize(values));
            }
        }
        return rows.Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Distinguishes the mandatory all-source validation from selected receipt reads.</summary>
    private sealed class SourceReads : DbCommandInterceptor
    {
        internal int SnapshotReads { get; private set; }
        internal int FullObservationReads { get; private set; }
        internal int SelectedObservationReads { get; private set; }
        internal Action? BeforeSelectedRead { get; init; }

        /// <summary>Counts source reads before execution and optionally fails inside the materialization transaction.</summary>
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"SourceSnapshots\"", StringComparison.Ordinal)) SnapshotReads++;
            if (command.CommandText.Contains("FROM \"SourceObservations\"", StringComparison.Ordinal))
            {
                if (command.CommandText.Contains("\"ObservationId\" =", StringComparison.Ordinal))
                {
                    SelectedObservationReads++;
                    BeforeSelectedRead?.Invoke();
                }
                else FullObservationReads++;
            }
            return ValueTask.FromResult(result);
        }
    }
}
