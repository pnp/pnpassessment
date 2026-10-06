using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Tests.Discovery;
using PnP.Scanning.Core.Tests.Fixtures;
using System.Security.Cryptography;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace PnP.Scanning.Core.Tests.Storage;

[Trait("Category", "PageInherits")]
public sealed class PageInheritsIntegratedRollbackTests
{
    private readonly ITestOutputHelper output;
    public PageInheritsIntegratedRollbackTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public async Task Integrated_backup_restore_reopens_bytes_frozen_configuration_and_exact_native_CSV_while_downgrade_loses_only_the_appended_delta()
    {
        const string raw = "  Synthetic.Backup, \"quoted\"\r\nType  ";
        var text = "<%@ Page Inherits='" + raw + "' %>";
        var declared = PageInheritsIntegratedTests.Make(81, "backup-declared.aspx", "Pages", text) with
            { Bytes = new byte[] { 0xfe, 0xff }.Concat(Encoding.BigEndianUnicode.GetBytes(text)).ToArray() };
        var configured = PageInheritsIntegratedTests.Make(82, "backup-configured.aspx", "Forms", "<%@ Page %>");
        var denied = PageInheritsIntegratedTests.Make(83, "backup-denied.aspx", "Views", null);
        using var fixture = new PageInheritsIntegrationFixture();
        await fixture.InitializeAsync(new PageBaseTypeConfiguration(new[]
        {
            PageInheritsIntegratedTests.Configuration(configured, "EffectiveOverride", "Synthetic.Configured", "Synthetic backup configuration"),
        }));
        var inputs = new[] { declared, configured, denied };
        await fixture.RunAsync(inputs.Select(value => value.Record()), async (row, token) =>
        {
            if (row.FileName == denied.Name) throw new UnauthorizedAccessException("Synthetic backup denial");
            var bytes = inputs.Single(value => value.Name == row.FileName).Bytes;
            return await AspxSourceReader.ReadAsync(row.DiscoveryObservation, row.DiscoveryObservation.Identity,
                new(PageSourcePersistenceFixture.ReadTime, "\"synthetic-backup-version\"", 2, 3),
                _ => Task.FromResult<Stream>(new MemoryStream(bytes)), token, bytes.LongLength);
        });
        var originalCsv = await fixture.Database.ExportAsync();
        originalCsv.Single(value => value["FileName"] == configured.Name)["TypeSource"].Should().Be("ConfiguredDefault");
        originalCsv.Single(value => value["FileName"] == configured.Name)["PublishingLayoutFamily"].Should().Be("Unknown");
        originalCsv.Single(value => value["FileName"] == denied.Name)["SourceReadState"].Should().Be("Denied");
        var backupFile = Path.Combine(fixture.Database.DirectoryPath, "consistent-backup.db");
        using (var db = fixture.Database.CreateContext())
        {
            await db.Database.OpenConnectionAsync();
            var source = (SqliteConnection)db.Database.GetDbConnection();
            using (var wal = source.CreateCommand()) { wal.CommandText = "PRAGMA journal_mode=WAL"; wal.ExecuteScalar().Should().Be("wal"); }
            var tables = new[] { "Scans", "ClassicPageDiscoveries", "ClassicPages", "TestDelays" };
            var oldColumns = tables.ToDictionary(table => table, table => PageSourceUpgradeTests.Columns(source, table)
                .Where(name => name is not ("SourceEvidenceJson" or "PageSourceEvidenceVersion" or "PageBaseTypeConfigurationJson")).ToArray());
            var before = tables.ToDictionary(table => table, table => PageSourceUpgradeTests.Snapshot(source, table, oldColumns[table]));
            using (var backup = new SqliteConnection($"Data Source={backupFile};Pooling=False"))
            {
                await backup.OpenAsync();
                source.BackupDatabase(backup);
                using var integrity = backup.CreateCommand();
                integrity.CommandText = "PRAGMA integrity_check";
                integrity.ExecuteScalar().Should().Be("ok");
            }
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(PageSourceUpgradeTests.IntegrationSchema);
            foreach (var table in tables)
            {
                PageSourceUpgradeTests.Columns(source, table).Should().BeEquivalentTo(oldColumns[table]);
                PageSourceUpgradeTests.Snapshot(source, table, oldColumns[table]).Should().Equal(before[table],
                    "every inherited value must survive removal of the appended columns");
            }
            await migrator.MigrateAsync();
            db.ChangeTracker.Clear();
            var scan = await db.Scans.AsNoTracking().SingleAsync();
            scan.PageSourceEvidenceVersion.Should().Be(0);
            scan.PageBaseTypeConfigurationJson.Should().BeNull();
            var lost = await db.ClassicPageDiscoveries.AsNoTracking().Where(value => value.RowType == "Page").ToArrayAsync();
            lost.Should().HaveCount(3).And.OnlyContain(value => value.SourceEvidenceJson == null && value.SourceRawDigest == null &&
                value.DeclaredInherits == null && value.BaseType == null && value.TypeSource == null &&
                value.SourceEvidenceState == "NotCollected");
        }
        var downgradedAndUpgradedCsv = await fixture.Database.ExportAsync();
        foreach (var original in originalCsv)
        {
            var current = downgradedAndUpgradedCsv.Single(value => value["RecordKey"] == original["RecordKey"]);
            foreach (var name in PageSourceCsvTests.OriginalHeaders) current[name].Should().Be(original[name], name);
            if (current["RowType"] == "Page")
            {
                current["SourceRawDigest"].Should().Be("");
                current["TypeSource"].Should().Be("");
                current["ObservedHandlerState"].Should().Be("", "forward migration does not recreate observations lost in downgrade");
            }
        }
        // All original handles have closed. Restore the consistent backup, not an active-WAL file copy.
        using (var backup = new SqliteConnection($"Data Source={backupFile};Pooling=False"))
        using (var target = new SqliteConnection($"Data Source={fixture.Database.DatabasePath};Pooling=False"))
        {
            await backup.OpenAsync();
            await target.OpenAsync();
            backup.BackupDatabase(target);
        }
        using (var db = fixture.Database.CreateContext())
        {
            var restored = await db.Scans.AsNoTracking().SingleAsync();
            restored.PageSourceEvidenceVersion.Should().Be(1);
            restored.PageBaseTypeConfigurationJson.Should().Be(fixture.Scan.PageBaseTypeConfigurationJson);
            db.Database.GetAppliedMigrations().Last().Should().EndWith("_ClassicPageSourceEvidence");
        }
        var restoredCsv = await fixture.Database.ExportAsync();
        restoredCsv.Should().HaveCount(originalCsv.Count);
        foreach (var original in originalCsv)
            restoredCsv.Single(value => value["RecordKey"] == original["RecordKey"]).Should().BeEquivalentTo(original,
                "restore must reproduce every native CSV field, not just convenience types");
        foreach (var input in inputs)
        {
            var key = fixture.AcquiredRows.Single(value => value.FileName == input.Name).RecordKey;
            var reopened = await fixture.Database.ReopenAsync(key);
            var csv = restoredCsv.Single(value => value["RecordKey"] == key);
            await PageInheritsIntegratedTests.VerifyBytesAndIndependentJsonAsync(fixture, reopened, csv,
                input.Bytes, input == declared ? raw : null);
            if (input.Bytes != null)
                reopened.SourceRawDigest.Should().Be(Convert.ToHexString(SHA256.HashData(
                    await fixture.Writer.ReadSourceArtifactAsync(fixture.Database.ScanId, key, reopened.SourceObservationId))));
        }
        fixture.Http.Transport.Requests.Should().BeEmpty();
        Directory.GetFiles(fixture.Database.DirectoryPath, "*.bin").Should().BeEmpty("original bytes are embedded in assessment.db; no sidecars are omitted");
        output.WriteLine("Integrated input -> adapter/parser -> native writer -> reopen -> CSV -> consistent WAL BackupDatabase -> Down/Up -> restore -> reopen/retrieve/independent CSV: PASS.");
        output.WriteLine("Down loses SourceEvidenceJson (all original/partial bytes, discoveries, read identities/version/time, digest/encoding, declaration/default and parse/configuration/Handler-availability evidence), PageSourceEvidenceVersion and PageBaseTypeConfigurationJson.");
        output.WriteLine("Up after Down is not restore: CP1 observations stay null/uncollected and scan version 0; inherited columns/CSV remain unchanged.");
        output.WriteLine("Unwind later dependent consumers before removing the shared source foundation. This fixture does not change shared refs or execute live acquisition.");
    }
}
