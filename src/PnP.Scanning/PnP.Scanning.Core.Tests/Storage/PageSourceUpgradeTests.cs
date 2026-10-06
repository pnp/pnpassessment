using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Configuration;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Storage.DatabaseMigration;
using PnP.Scanning.Core.Tests.Fixtures;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace PnP.Scanning.Core.Tests.Storage;

[Trait("Category", "PageInherits")]
public sealed class PageSourceUpgradeTests
{
    internal const string IntegrationSchema = "20260928091529_ClassicPublishingLayoutTypeEvidence";
    private readonly ITestOutputHelper output;
    public PageSourceUpgradeTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public void Reviewed_migration_is_exactly_three_additive_columns_and_matches_the_Release_model()
    {
        var migration = new ClassicPageSourceEvidence();
        migration.UpOperations.Should().HaveCount(3).And.OnlyContain(operation => operation is AddColumnOperation);
        var additions = migration.UpOperations.Cast<AddColumnOperation>().ToArray();
        additions.Select(value => value.Table + "." + value.Name).Should().BeEquivalentTo(new[]
        {
            "Scans.PageSourceEvidenceVersion", "Scans.PageBaseTypeConfigurationJson", "ClassicPageDiscoveries.SourceEvidenceJson",
        });
        additions.Single(value => value.Name == "PageSourceEvidenceVersion").DefaultValue.Should().Be(0);
        additions.Where(value => value.Name != "PageSourceEvidenceVersion").Should()
            .OnlyContain(value => value.IsNullable && value.DefaultValue == null);
        migration.DownOperations.Should().HaveCount(3).And.OnlyContain(operation => operation is DropColumnOperation);
        using var fixture = new PageSourcePersistenceFixture();
        using var db = fixture.CreateContext();
        db.Database.HasPendingModelChanges().Should().BeFalse("the generated snapshot must match Release, including the inherited TestDelays table");
        var latest = db.Database.GetMigrations().Last();
        latest.Should().EndWith("_ClassicPageSourceEvidence");
        var migrator = db.GetService<IMigrator>();
        var up = migrator.GenerateScript(IntegrationSchema, latest);
        up.Should().NotContain("DROP ").And.NotContain("RENAME ").And.NotContain("UPDATE ").And.NotContain("DELETE ");
        up.Should().Contain("ADD \"SourceEvidenceJson\" TEXT NULL").And.Contain("ADD \"PageBaseTypeConfigurationJson\" TEXT NULL")
            .And.Contain("ADD \"PageSourceEvidenceVersion\" INTEGER NOT NULL DEFAULT 0");
        var down = migrator.GenerateScript(latest, IntegrationSchema);
        output.WriteLine("ReviewedUpSql:\n" + up);
        output.WriteLine("ReviewedDownSql:\n" + down);
        output.WriteLine("Up: three AddColumn operations only. Down: exactly those three columns, using SQLite table rebuilds.");
    }

    [Theory]
    [InlineData("20260918022217_ClassicAspxDiscovery", false)]
    [InlineData("20260924172015_ClassicAssetPurpose", false)]
    [InlineData("20260924173030_ClassicPublishingLayoutReference", false)]
    [InlineData(IntegrationSchema, true)]
    public async Task Historical_and_handed_over_schemas_keep_old_results_and_never_backfill_CP1_from_absence_or_SourceHash(
        string baseline, bool inheritedFamilyEvidence)
    {
        using var fixture = new PageSourcePersistenceFixture(migrate: false);
        using var db = fixture.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(baseline);
        await db.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO Scans
            (ScanId, StartDate, EndDate, Status, PreScanStatus, PostScanStatus, CLIThreads, CLIMode, CLIAuthMode,
             CLITenant, CLIApplicationId, CLITenantId, CLIEnvironment, CLICertPath, CLICertFile, CLICertFilePassword)
            VALUES ({fixture.ScanId}, '2026-01-01', '2026-01-01', 0, 0, 0, 1, 'Classic', 'application', '', '', '', '', '', '', '')");
        await db.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO ClassicPageDiscoveries
            (ScanId, RecordKey, RowType, ScopeType, DiscoveryStatus, AssessmentStatus, ObservedAtUtc, SiteUrl,
             WebUrl, Url, FileName, FileUniqueId, ListItemId, PageType, ContentTypeId, ErrorDetail, EvidenceJson)
            VALUES ({fixture.ScanId}, 'page:historical', 'Page', 'File', 'Denied', 'Complete', '2026-01-02',
                {PageSourcePersistenceFixture.Site}, {PageSourcePersistenceFixture.Web}, '/sites/source/Pages/historical.aspx',
                'historical.aspx', {PageSourcePersistenceFixture.FileId}, 7, 'PublishingPage',
                {AspxAssetPurpose.PublishingContentType}, {"Synthetic, \"historical\"\nreason"}, '{{""historical"":true}}')");
        await db.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO ClassicPages
            (ScanId, SiteUrl, WebUrl, PageUrl, ListId, PageType, Layout, DiscoveryStatus, AssessmentStatus,
             FileUniqueId, ListItemId, ModifiedAt, HomePage, UncustomizedHomePage, WebPartCount, MappingPercentage, UnmappedWebParts)
            VALUES ({fixture.ScanId}, {PageSourcePersistenceFixture.Site}, {PageSourcePersistenceFixture.Web},
                '/sites/source/Pages/historical.aspx', {Guid.Empty}, 'PublishingPage', 'HistoricalLayout', 'Denied', 'Complete',
                {PageSourcePersistenceFixture.FileId}, 7, '2026-01-02', NULL, 0, 7, 42, 'HistoricalUnmapped')");
        db.TestDelays.Add(new TestDelay
        {
            ScanId = fixture.ScanId, SiteUrl = PageSourcePersistenceFixture.Site, WebUrl = PageSourcePersistenceFixture.Web,
            Delay1 = 11, Delay2 = 12, Delay3 = 13, WebIdString = "Synthetic historical debug evidence",
        });
        await db.SaveChangesAsync();
        if (inheritedFamilyEvidence)
        {
            const string historical = """
                [{"Declaration":"Historical.Direct","ResolvedIdentity":null,"SourceHash":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                "SourceStatus":"Available","ResolutionStatus":"Unknown","Decision":"Unknown","Reason":"InheritsMissing","Ancestry":[],"CatalogErrors":[]}]
                """;
            await db.Database.ExecuteSqlInterpolatedAsync($@"UPDATE ClassicPageDiscoveries SET
                DeclaredPageType='Historical.Direct', PageTypeEvidenceJson={historical}, PageTypeEvidenceOrigin='DeclaredSource',
                PageTypeSourceStatus='Available', PageTypeReason='InheritsMissing' WHERE ScanId={fixture.ScanId}");
            await db.Database.ExecuteSqlInterpolatedAsync($@"UPDATE Scans SET PublishingLayoutRuleVersion=1,
                PublishingLayoutTypeCatalogJson={new PublishingLayoutTypeCatalog().ToJson()} WHERE ScanId={fixture.ScanId}");
        }
        await db.Database.OpenConnectionAsync();
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        var tables = new[] { "Scans", "ClassicPageDiscoveries", "ClassicPages", "TestDelays" };
        var columns = tables.ToDictionary(table => table, table => Columns(connection, table));
        var original = tables.ToDictionary(table => table, table => Snapshot(connection, table, columns[table]));
        var sql = migrator.GenerateScript(baseline);
        sql.Should().NotContain("DROP ").And.NotContain("RENAME ").And.NotContain("UPDATE ").And.NotContain("DELETE ");
        await migrator.MigrateAsync();
        foreach (var table in tables)
        {
            Columns(connection, table).Should().Contain(columns[table]);
            Snapshot(connection, table, columns[table]).Should().Equal(original[table], "every old column and value must survive upgrade");
        }
        db.ChangeTracker.Clear();
        var scan = await db.Scans.AsNoTracking().SingleAsync();
        scan.PageSourceEvidenceVersion.Should().Be(0);
        scan.PageBaseTypeConfigurationJson.Should().BeNull();
        var row = await db.ClassicPageDiscoveries.AsNoTracking().SingleAsync();
        row.SourceEvidenceJson.Should().BeNull();
        row.SourceEvidenceState.Should().Be("NotCollected");
        row.DeclaredInherits.Should().BeNull();
        row.BaseType.Should().BeNull();
        row.TypeSource.Should().BeNull();
        row.FrameworkDefaultAssumption.Should().BeNull();
        row.SourceReadState.Should().BeNull();
        row.PageParseState.Should().BeNull();
        row.SourceRawDigestAlgorithm.Should().BeNull();
        row.SourceRawDigest.Should().BeNull();
        row.SourceCapturedByteLength.Should().BeNull();
        row.SourceArtifactReference.Should().BeNull();
        row.DiscoveryStatus.Should().Be("Denied");
        row.AssessmentStatus.Should().Be("Complete");
        row.PageType.Should().Be("PublishingPage");
        if (inheritedFamilyEvidence)
        {
            row.DeclaredPageType.Should().Be("Historical.Direct");
            row.PageTypeEvidenceJson.Should().Contain("\"SourceHash\":\"AAAAAAAA");
            row.PageTypeSourceStatus.Should().Be("Available", "the old decoded-text facet stays recognizable, not a new raw read");
        }
        await fixture.Writer().FinalizeScanAsync(fixture.ScanId);
        await fixture.Writer().FinalizeScanAsync(fixture.ScanId);
        var exported = (await fixture.ExportAsync()).Single(value => value["RowType"] == "Page");
        exported["FileName"].Should().Be("historical.aspx");
        exported["DiscoveryStatus"].Should().Be("Denied");
        exported["AssessmentStatus"].Should().Be("Complete");
        exported["PageTypeEvidenceOrigin"].Should().Be(inheritedFamilyEvidence ? "DeclaredSource" : "None");
        exported["SourceEvidenceState"].Should().Be("NotCollected");
        foreach (var name in new[] { "SourceEvidenceJson", "SourceRawDigest", "SourceReadState", "PageParseState",
            "DeclaredInherits", "BaseType", "TypeSource", "ConfigurationKnowledgeState", "ObservedHandlerState" })
            exported[name].Should().Be("", "uncollected historical evidence is not manufactured");
        var headers = PageSourcePersistenceFixture.ReadIndependentCsv(Path.Combine(fixture.DirectoryPath, "report", "discovery.csv")).Headers;
        headers.Take(39).Should().Equal(PageSourceCsvTests.OriginalHeaders);
        await migrator.MigrateAsync(baseline);
        foreach (var table in new[] { "Scans", "ClassicPages", "TestDelays" })
            Snapshot(connection, table, columns[table]).Should().Equal(original[table]);
        // Finalization added one Summary, but did not modify the original Page.
        Snapshot(connection, "ClassicPageDiscoveries", columns["ClassicPageDiscoveries"]).First().Should().Be(original["ClassicPageDiscoveries"].Single());
        // SQLite rebuilds tables on Down and may reorder physical columns. Compare names as a set;
        // snapshots above still select every original column by name and compare every value.
        Columns(connection, "ClassicPageDiscoveries").Should().BeEquivalentTo(columns["ClassicPageDiscoveries"]);
        Columns(connection, "Scans").Should().BeEquivalentTo(columns["Scans"]);
        await migrator.MigrateAsync();
        db.ChangeTracker.Clear();
        (await db.Scans.AsNoTracking().SingleAsync()).PageSourceEvidenceVersion.Should().Be(0);
        (await db.ClassicPageDiscoveries.AsNoTracking().SingleAsync(value => value.RowType == "Page")).SourceEvidenceJson.Should().BeNull();
        output.WriteLine($"Executed Up/Down/Up for {baseline}; old columns/results including TestDelays retained; CP1 fields remain uncollected.");
    }

    [Fact]
    public async Task SQLite_consistent_backup_restore_includes_bytes_and_configuration_and_downgrade_removes_only_CP1_columns()
    {
        using var fixture = new PageSourcePersistenceFixture();
        var scan = await fixture.SeedAsync(PageSourcePersistenceTests.ApplicableConfiguration());
        var row = fixture.Page();
        var bytes = new byte[] { 0xef, 0xbb, 0xbf }.Concat(Encoding.UTF8.GetBytes("<%@ Page Inherits='Synthetic.Backup' %>")).ToArray();
        await fixture.AcquireAsync(scan, row, await PageSourcePersistenceFixture.Capture(row, bytes));
        var before = await fixture.ReopenAsync(row.RecordKey);
        var source = before.SourceEvidenceJson;
        var observationId = before.SourceObservationId;
        var backupFile = Path.Combine(fixture.DirectoryPath, "backup.db");
        Dictionary<string, string[]> original;
        Dictionary<string, string[]> columns;
        using (var db = fixture.CreateContext())
        {
            await db.Database.OpenConnectionAsync();
            var connection = (SqliteConnection)db.Database.GetDbConnection();
            // Exercise backup while SQLite is in WAL mode: a blind copy of only the main file is not a recipe.
            using (var command = connection.CreateCommand()) { command.CommandText = "PRAGMA journal_mode=WAL"; command.ExecuteScalar(); }
            columns = new[] { "Scans", "ClassicPageDiscoveries" }.ToDictionary(table => table,
                table => Columns(connection, table).Where(name => name is not ("SourceEvidenceJson" or "PageSourceEvidenceVersion" or "PageBaseTypeConfigurationJson")).ToArray());
            original = columns.ToDictionary(pair => pair.Key, pair => Snapshot(connection, pair.Key, pair.Value));
            using (var backup = new SqliteConnection($"Data Source={backupFile};Pooling=False"))
            {
                await backup.OpenAsync();
                connection.BackupDatabase(backup);
            }
            await db.GetService<IMigrator>().MigrateAsync(IntegrationSchema);
            foreach (var pair in columns)
            {
                Columns(connection, pair.Key).Should().BeEquivalentTo(pair.Value);
                Snapshot(connection, pair.Key, pair.Value).Should().Equal(original[pair.Key]);
            }
            await db.GetService<IMigrator>().MigrateAsync();
            db.ChangeTracker.Clear();
            (await db.Scans.AsNoTracking().SingleAsync()).PageSourceEvidenceVersion.Should().Be(0);
            (await db.Scans.AsNoTracking().SingleAsync()).PageBaseTypeConfigurationJson.Should().BeNull();
            var lost = await db.ClassicPageDiscoveries.AsNoTracking().SingleAsync();
            lost.SourceEvidenceJson.Should().BeNull();
            lost.SourceRawDigest.Should().BeNull();
            lost.DeclaredInherits.Should().BeNull();
            lost.PageTypeEvidenceJson.Should().NotBeNullOrEmpty("downgrade retains the inherited decoded-text/family facet, not CP1 raw bytes");
        }
        // Restore the consistent backup after all original database handles have closed.
        using (var backup = new SqliteConnection($"Data Source={backupFile};Pooling=False"))
        using (var restored = new SqliteConnection($"Data Source={fixture.DatabasePath};Pooling=False"))
        {
            await backup.OpenAsync();
            await restored.OpenAsync();
            backup.BackupDatabase(restored);
        }
        var after = await fixture.ReopenAsync(row.RecordKey);
        after.SourceEvidenceJson.Should().Be(source);
        after.SourceObservationId.Should().Be(observationId);
        after.TypeSource.Should().Be("Declared");
        after.DeclaredInherits.Should().Be("Synthetic.Backup");
        var retrieved = await fixture.Writer().ReadSourceArtifactAsync(fixture.ScanId, row.RecordKey, observationId);
        retrieved.Should().Equal(bytes);
        after.SourceRawDigest.Should().Be(Convert.ToHexString(SHA256.HashData(retrieved)));
        using (var db = fixture.CreateContext())
        {
            var restoredScan = await db.Scans.AsNoTracking().SingleAsync();
            restoredScan.PageSourceEvidenceVersion.Should().Be(1);
            restoredScan.PageBaseTypeConfigurationJson.Should().Be(scan.PageBaseTypeConfigurationJson);
        }
        output.WriteLine("Executed consistent WAL-mode SQLite BackupDatabase, CP1 Down/Up, closed handles, restore, reopen, byte retrieval and SHA256 verification.");
        output.WriteLine("No raw-byte sidecars exist: SourceEvidenceJson contains all original bytes in assessment.db. Downgrade discards this document and both scan CP1 columns.");
    }

    internal static string[] Columns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\")";
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read()) columns.Add(reader.GetString(1));
        return columns.ToArray();
    }

    internal static string[] Snapshot(SqliteConnection connection, string table, IReadOnlyList<string> columns)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {string.Join(',', columns.Select(name => '"' + name + '"'))} FROM \"{table}\" ORDER BY 1,2";
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
            values.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(index =>
                reader.IsDBNull(index) ? "<SQL-NULL>" : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture))));
        return values.ToArray();
    }
}
