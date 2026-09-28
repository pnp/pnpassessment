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
using Xunit;
using static PnP.Scanning.Core.Tests.Discovery.PublishingLayoutTypeEvidenceTests;

namespace PnP.Scanning.Core.Tests.Storage;

public sealed class PublishingLayoutTypeUpgradeTests
{
    [Fact]
    public async Task Current_layout_reference_schema_upgrades_additively_without_backfilling_authority_or_evidence()
    {
        const string baseline = "20260924173030_ClassicPublishingLayoutReference";
        var scan = Guid.NewGuid();
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using var db = new ScanContext(new DbContextOptionsBuilder<ScanContext>().UseSqlite(connection).Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(baseline);
        await db.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO Scans
            (ScanId, StartDate, EndDate, Status, PreScanStatus, PostScanStatus, CLIThreads)
            VALUES ({scan}, '2026-01-01', '2026-01-01', 0, 0, 0, 1)");
        await db.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO ClassicPageDiscoveries
            (ScanId, RecordKey, RowType, DiscoveryStatus, AssessmentStatus, ObservedAtUtc,
             PageType, ContentTypeId, AssetPurpose, AssetPurposeStatus, AssetPurposeReason, ErrorDetail)
            VALUES ({scan}, 'page:historical', 'Page', 'Denied', 'Unknown', '2026-01-01',
                'PublishingPage', {AspxAssetPurpose.LayoutContentType}, 'PageLayout', 'Confirmed', 'PageLayoutContentType', 'Original denial')");
        new ClassicPublishingLayoutTypeEvidence().UpOperations.Should().HaveCount(10)
            .And.OnlyContain(operation => operation is AddColumnOperation);
        var sql = migrator.GenerateScript(baseline);
        sql.Should().NotContain("DROP ").And.NotContain("RENAME ").And.NotContain("UPDATE ").And.NotContain("DELETE ");
        await migrator.MigrateAsync();
        var authority = await db.Scans.SingleAsync();
        authority.PublishingLayoutRuleVersion.Should().Be(0);
        authority.PublishingLayoutTypeCatalogJson.Should().BeNull();
        PublishingLayoutTypeEvidence.ForScan(authority, (_, _) => throw new InvalidOperationException("Legacy must not read source")).Should().BeNull();
        var row = await db.ClassicPageDiscoveries.SingleAsync();
        row.PublishingLayoutFamily.Should().Be("Unknown");
        row.PageTypeSourceStatus.Should().Be("Unknown");
        row.PageTypeResolutionStatus.Should().Be("Unknown");
        row.PageTypeReason.Should().Be("NotEvaluated");
        row.PageTypeEvidenceOrigin.Should().Be("None");
        row.PageTypeEvidenceJson.Should().BeNull();
        row.DeclaredPageType.Should().BeNull();
        row.ResolvedPageType.Should().BeNull();
        row.PageType.Should().Be("PublishingPage");
        row.ContentTypeId.Should().Be(AspxAssetPurpose.LayoutContentType);
        row.AssetPurpose.Should().Be("PageLayout");
        row.AssetPurposeStatus.Should().Be("Confirmed");
        row.AssetPurposeReason.Should().Be("PageLayoutContentType");
        row.DiscoveryStatus.Should().Be("Denied");
        row.AssessmentStatus.Should().Be("Unknown");
        row.ErrorDetail.Should().Be("Original denial");
    }

    [Fact]
    public async Task New_scan_captures_configured_metadata_and_restart_preserves_authority_without_reopening_assemblies()
    {
        using var metadata = new MetadataFixture();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["PublishingLayoutTypeEvidence:Assemblies:0"] = metadata.Path,
        }).Build();
        var storage = new StorageManager(new EphemeralDataProtectionProvider(), config);
        var scan = Guid.NewGuid();
        await storage.LaunchNewScanAsync(scan, new StartRequest { Mode = "Classic", AuthMode = "application", Threads = 1 }, new());
        string snapshot;
        using (var db = new ScanContext(scan))
        {
            var authority = await db.Scans.SingleAsync();
            authority.PublishingLayoutRuleVersion.Should().Be(1);
            snapshot = authority.PublishingLayoutTypeCatalogJson;
            snapshot.Should().Contain("ECMA335:").And.Contain("SHA256=").And.NotContain(metadata.Path);
            db.ClassicPageDiscoveries.Add(new ClassicPageDiscovery
            {
                ScanId = scan, RecordKey = "scope:denied", RowType = "Scope", DiscoveryStatus = "Denied",
                ErrorDetail = "Original denial",
            });
            await db.SaveChangesAsync();
        }
        // The persisted catalog, not the configuration of a later worker, owns the authority.
        File.Delete(metadata.Path);
        config["PublishingLayoutTypeEvidence:Assemblies:0"] = "unavailable.dll";
        await storage.ConsolidatedScanToEnableRestartAsync(scan);
        await storage.RestartScanAsync(scan);
        using (var db = new ScanContext(scan))
        {
            var authority = await db.Scans.SingleAsync();
            authority.PublishingLayoutRuleVersion.Should().Be(1);
            authority.PublishingLayoutTypeCatalogJson.Should().Be(snapshot);
            var inspect = PublishingLayoutTypeEvidence.ForScan(authority, (_, _) => Task.FromResult(Source(Identity("Indirect"))));
            var row = new ClassicPageDiscovery { RowType = "Page" };
            await inspect(row, default);
            PublishingLayoutTypeEvidence.IsConfirmedMember(row).Should().BeTrue();
            (await db.ClassicPageDiscoveries.SingleAsync()).DiscoveryStatus.Should().Be("Denied");
            // A recorded historical authority is also preserved by the exact restart path.
            authority.PublishingLayoutRuleVersion = 0;
            authority.PublishingLayoutTypeCatalogJson = null;
            await db.SaveChangesAsync();
        }
        await storage.ConsolidatedScanToEnableRestartAsync(scan);
        await storage.RestartScanAsync(scan);
        using var read = new ScanContext(scan);
        (await read.Scans.SingleAsync()).PublishingLayoutRuleVersion.Should().Be(0);
        (await read.Scans.SingleAsync()).PublishingLayoutTypeCatalogJson.Should().BeNull();
    }
}
