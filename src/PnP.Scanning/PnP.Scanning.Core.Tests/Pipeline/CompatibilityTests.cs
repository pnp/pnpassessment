#nullable enable
using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PnP.Scanning.Core.Pipeline.Orchestration;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Storage.Pipeline;
using Scan = PnP.Scanning.Core.Storage.Scan;
using Xunit;

namespace PnP.Scanning.Core.Tests.Pipeline;

[Trait("Category", "Pipeline")]
public sealed class CompatibilityTests
{
    private const string MainMigration = "20260918022217_ClassicAspxDiscovery";
    private const string HistoricalMigration = "20260928091529_ClassicPublishingLayoutTypeEvidence";

    [Fact]
    public async Task Startup_recovery_settles_legacy_running_rows_without_touching_pipeline_phases_or_online_services()
    {
        using var data = new StoreCase("pipeline-startup-recovery");
        var pipeline = await data.UnsealedAsync();
        var legacy = Guid.NewGuid();
        await data.Store.EnsureDatabaseAsync(legacy, default, create: true);
        using (var db = data.Store.CreateContext(legacy))
        {
            db.Scans.Add(new Scan { ScanId = legacy, CLIMode = "Classic", Status = ScanStatus.Running, StartDate = DateTime.Now });
            await db.SaveChangesAsync();
        }
        await new LegacyAssessmentRecovery(data.Store).StartAsync(default);
        using var legacyRead = data.Store.CreateContext(legacy);
        Assert.Equal(ScanStatus.Terminated, (await legacyRead.Scans.SingleAsync()).Status);
        Assert.Single(await legacyRead.History.ToListAsync());
        using var pipelineRead = data.Store.CreateContext(pipeline.Assessment);
        Assert.Equal(ScanStatus.Running, (await pipelineRead.Scans.SingleAsync()).Status);
        Assert.Equal(ScanStatus.Running, (await pipelineRead.PhaseRuns.SingleAsync()).Status);
        Assert.Equal(6, await pipelineRead.SourceObservations.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Incremental_upgrade_reopen_preserves_legacy_rows_reports_counts_and_inert_history(bool historicalColumns)
    {
        using var data = new StoreCase(historicalColumns ? "pipeline-historical" : "pipeline-main");
        var id = Guid.NewGuid();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(data.Store.DatabasePath(id))!);
        using (var db = data.Store.CreateContext(id))
        {
            await db.GetService<IMigrator>().MigrateAsync(MainMigration);
            db.Scans.Add(new Scan
            {
                ScanId = id, CLIMode = "Classic", Status = ScanStatus.Finished, CLIThreads = 4, Version = "legacy-fixture",
                StartDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), EndDate = new DateTime(2026, 9, 1, 1, 0, 0, DateTimeKind.Utc),
            });
            db.SiteCollections.AddRange(new SiteCollection { ScanId = id, SiteUrl = "https://legacy.invalid/sites/a", Status = SiteWebStatus.Finished },
                new SiteCollection { ScanId = id, SiteUrl = "https://legacy.invalid/sites/b", Status = SiteWebStatus.Failed });
            var pages = new[] { "WikiPage", "PublishingPage", "ModernPage" }.Select((type, index) => new ClassicPage
            {
                ScanId = id, SiteUrl = "https://legacy.invalid/sites/a", WebUrl = "/",
                PageUrl = $"/SitePages/{index}.aspx", PageName = $"{index}.aspx", PageType = type,
                HomePage = index == 0, RemediationCode = "CP2", WebPartCount = index + 1, MappingPercentage = 50,
                ModifiedAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            }).ToArray();
            db.ClassicPages.AddRange(pages.Where(x => x.AddToDatabase()));
            db.ClassicPageDiscoveries.Add(new ClassicPageDiscovery
            {
                ScanId = id, RecordKey = "legacy-discovery", RowType = "Page", DiscoveryStatus = "Observed",
                SiteUrl = "https://legacy.invalid/sites/a", WebUrl = "/", Url = "/SitePages/0.aspx",
                PageType = "WikiPage", ObservedAtUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            db.Set<TestDelay>().Add(new TestDelay
            {
                ScanId = id, SiteUrl = "https://legacy.invalid/sites/a", WebUrl = "/", Delay1 = 11, Delay2 = 22, Delay3 = 33,
            });
            await db.SaveChangesAsync();
            if (historicalColumns)
            {
                await db.Database.ExecuteSqlRawAsync(await File.ReadAllTextAsync(System.IO.Path.Combine(AppContext.BaseDirectory,
                    "Pipeline", "Fixtures", "legacy-publishing-layout-evidence.sql")));
                var catalog = "{\"legacy\":true}";
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Scans SET PublishingLayoutRuleVersion = {77}, PublishingLayoutTypeCatalogJson = {catalog}");
            }
        }
        var beforePath = System.IO.Path.Combine(data.DirectoryPath, "report-before");
        var afterPath = System.IO.Path.Combine(data.DirectoryPath, "report-after");
        var before = await ExportAsync(data.Store, id, beforePath);
        var reopened = new PipelineStore(data.DirectoryPath);
        await reopened.EnsureDatabaseAsync(id, default);
        var after = await ExportAsync(reopened, id, afterPath);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var file in before.Keys) Assert.Equal(before[file], after[file]);
        using var read = reopened.CreateContext(id);
        Assert.Equal(2, await read.ClassicPages.CountAsync());
        Assert.Equal(new[] { "PublishingPage", "WikiPage" }, await read.ClassicPages.OrderBy(x => x.PageType).Select(x => x.PageType).ToArrayAsync());
        Assert.Equal(1, await read.ClassicPageDiscoveries.CountAsync());
        Assert.Equal(2, await read.SiteCollections.CountAsync());
        Assert.Equal(1, await read.SiteCollections.CountAsync(x => x.Status == SiteWebStatus.Finished));
        Assert.Equal(1, await read.SiteCollections.CountAsync(x => x.Status == SiteWebStatus.Failed));
        Assert.Equal(33, (await read.Set<TestDelay>().SingleAsync()).Delay3);
        Assert.Equal(ScanStatus.Finished, (await read.Scans.SingleAsync()).Status);
        Assert.Equal("Classic", (await read.Scans.SingleAsync()).CLIMode);
        Assert.Equal(0, await read.SourceSnapshots.CountAsync());
        Assert.Equal(0, await read.SourceObservations.CountAsync());
        Assert.Equal(0, await read.PhaseRuns.CountAsync());
        Assert.DoesNotContain(read.Model.GetEntityTypes().SelectMany(x => x.GetProperties()), x => x.Name == "PublishingLayoutRuleVersion");
        var migrations = (await read.Database.GetAppliedMigrationsAsync()).ToArray();
        Assert.Contains(MainMigration, migrations);
        Assert.Contains("20261010110520_ClassicPagePipeline", migrations);
        Assert.Contains("20220325101514_v0.2.0", migrations);
        if (historicalColumns)
        {
            Assert.Contains(HistoricalMigration, migrations);
            await read.Database.OpenConnectionAsync();
            using var command = read.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT PublishingLayoutRuleVersion, PublishingLayoutTypeCatalogJson FROM Scans";
            using var result = await command.ExecuteReaderAsync();
            Assert.True(await result.ReadAsync());
            Assert.Equal(77, result.GetInt32(0));
            Assert.Equal("{\"legacy\":true}", result.GetString(1));
        }
        var coordinator = new PipelineCoordinator(reopened, new ModuleRegistry(), new ForbiddenOnlineEnvironment { ForbidRestore = true });
        await Assert.ThrowsAsync<NotSupportedException>(() => coordinator.AnalyzeAsync(new()
        {
            Id = id.ToString(), SnapshotId = Guid.NewGuid().ToString(),
        }));
        Assert.Equal(0, await read.AnalysisRuns.CountAsync());
        var columns = ReadHeader(System.IO.Path.Combine(afterPath, "classicpages.csv"));
        Assert.Equal(new[]
        {
            "PageUrl", "PageName", "PageType", "ListUrl", "ListTitle", "ListId", "SiteCollectionId", "WebId", "FileUniqueId", "ListItemId",
            "DiscoveryStatus", "AssessmentStatus", "ModifiedAt", "Layout", "HomePage", "UncustomizedHomePage", "ModifiedBy", "WebPartCount",
            "MappingPercentage", "UnmappedWebParts", "RemediationCode", "ScanId", "SiteUrl", "WebUrl",
        }, columns);
    }

    private static async Task<Dictionary<string, byte[]>> ExportAsync(PipelineStore store, Guid id, string directory)
    {
        Directory.CreateDirectory(directory);
        using var db = store.CreateContext(id);
        await ReportManager.ExportClassicReportDataAsync(db, id, directory, new CsvConfiguration(CultureInfo.InvariantCulture) { Delimiter = "," });
        return Directory.GetFiles(directory).ToDictionary(x => System.IO.Path.GetFileName(x), File.ReadAllBytes);
    }

    private static string[] ReadHeader(string file)
    {
        using var reader = new StreamReader(file);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
        Assert.True(csv.Read());
        csv.ReadHeader();
        return csv.HeaderRecord!;
    }
}
