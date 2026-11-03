using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Storage;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery.Aspx;

public sealed class ClassicAspxConsolidationRetentionTests
{
    /// <summary>Exercises the real restart lifecycle: pending legacy results clear while ASPX and non-ASPX file facts, raw item/NoExecute metadata, and denial evidence survive.</summary>
    [Fact]
    public async Task ActualRestartRetainsPhysicalInventoryAndRawEvidence()
    {
        var scan = Guid.NewGuid();
        var site = Guid.NewGuid();
        var pendingWeb = Guid.NewGuid();
        var finishedWeb = Guid.NewGuid();
        var list = Guid.NewGuid();
        const string url = "https://example.invalid/sites/retention";
        var folder = Path.GetFullPath(StorageManager.GetScanDataFolder(scan));
        var expected = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, scan.ToString()));
        Assert.Equal(expected, folder);
        Assert.Equal(scan.ToString(), Path.GetFileName(folder));
        Assert.False(Directory.Exists(folder));
        var created = false;
        try
        {
            Directory.CreateDirectory(folder);
            created = true;
            using (var db = new ScanContext(scan))
            {
                await db.Database.MigrateAsync();
                db.SiteCollections.Add(new SiteCollection
                {
                    ScanId = scan, SiteUrl = url, Status = SiteWebStatus.Running, StartDate = DateTime.UtcNow
                });
                db.Webs.AddRange(
                    new Web
                    {
                        ScanId = scan, SiteUrl = url, WebUrl = "/", WebUrlAbsolute = url,
                        Status = SiteWebStatus.Running, StartDate = DateTime.UtcNow
                    },
                    new Web
                    {
                        ScanId = scan, SiteUrl = url, WebUrl = "/finished", WebUrlAbsolute = url + "/finished",
                        Status = SiteWebStatus.Finished
                    });
                db.ClassicPages.AddRange(
                    new ClassicPage
                    {
                        ScanId = scan, SiteUrl = url, WebUrl = "/", PageUrl = url + "/pending.aspx",
                        PageName = "pending.aspx", ListId = list
                    },
                    new ClassicPage
                    {
                        ScanId = scan, SiteUrl = url, WebUrl = "/finished", PageUrl = url + "/finished/kept.aspx",
                        PageName = "kept.aspx", ListId = list
                    });
                db.ClassicPageWebParts.Add(new ClassicPageWebPart
                {
                    ScanId = scan, SiteUrl = url, WebUrl = "/", PageUrl = url + "/pending.aspx", WebPartIndex = 0
                });
                db.ClassicLists.Add(new ClassicList { ScanId = scan, SiteUrl = url, WebUrl = "/", ListId = list });
                await db.SaveChangesAsync();
            }
            var pending = Inventory(scan, site, pendingWeb, list, "/sites/retention/pending.aspx", "1");
            var finished = Inventory(scan, site, finishedWeb, list, "/sites/retention/finished/retained.json", "0");
            await new SPFileWriter(scan).WriteAsync(new[] { pending, finished });
            var raw = new AssessmentDiscoveryWriter(scan);
            await raw.WriteAsync(new[]
            {
                new ClassicPageDiscovery
                {
                    ScanId = scan, SiteUrl = url, WebUrl = "/", RecordKey = "raw-pending-denied",
                    RowType = "Scope", ScopeType = "Surface", DiscoveryStatus = "Denied",
                    ErrorCodes = "synthetic_retained_denial", EvidenceJson = "{\"retained\":true}",
                    ObservedAtUtc = DateTime.UtcNow
                }
            });

            var storage = new StorageManager(new EphemeralDataProtectionProvider());
            await storage.ConsolidatedScanToEnableRestartAsync(scan);

            using var retained = new ScanContext(scan);
            Assert.Equal(SiteWebStatus.Queued, (await retained.SiteCollections.SingleAsync()).Status);
            Assert.Equal(SiteWebStatus.Queued, (await retained.Webs.SingleAsync(row => row.WebUrl == "/")).Status);
            Assert.Equal(DateTime.MinValue, (await retained.Webs.SingleAsync(row => row.WebUrl == "/")).StartDate);
            Assert.Equal(SiteWebStatus.Finished, (await retained.Webs.SingleAsync(row => row.WebUrl == "/finished")).Status);
            Assert.Empty(await retained.ClassicPages.Where(row => row.WebUrl == "/").ToArrayAsync());
            Assert.Empty(await retained.ClassicPageWebParts.ToArrayAsync());
            Assert.Empty(await retained.ClassicLists.ToArrayAsync());
            Assert.Single(await retained.ClassicPages.Where(row => row.WebUrl == "/finished").ToArrayAsync());
            var inventories = await retained.SPFiles.OrderBy(row => row.FileUniqueId).ToArrayAsync();
            Assert.Equal(2, inventories.Length);
            Assert.Contains(inventories, row => row.SiteCollectionId == pending.SiteCollectionId && row.FileUniqueId == pending.FileUniqueId && row.WebId == pendingWeb && row.ProvenanceJson == pending.ProvenanceJson);
            Assert.Contains(inventories, row => row.SiteCollectionId == finished.SiteCollectionId && row.FileUniqueId == finished.FileUniqueId && row.WebId == finishedWeb && row.ProvenanceJson == finished.ProvenanceJson);
            Assert.All(inventories, row =>
            {
                Assert.Equal("Observed", row.CoverageState);
                Assert.Equal(42, row.ListItemId);
                Assert.Equal(4096L, row.Length);
                Assert.Equal(row.FileUniqueId == pending.FileUniqueId ? "1" : "0", row.NoExecuteRaw);
                Assert.Equal("synthetic-noexecute-source", row.NoExecuteSource);
                Assert.Equal("Available", row.NoExecuteReadState);
                Assert.Equal("Available", row.ItemMetadataReadState);
                Assert.Equal("{\"WikiField\":\"value\"}", row.ItemMetadataFieldStatesJson);
                Assert.False(row.WikiFieldHasValue);
            });
            var denial = await retained.ClassicPageDiscoveries.SingleAsync(row => row.RecordKey == "raw-pending-denied");
            Assert.Equal("Denied", denial.DiscoveryStatus);
            Assert.Equal("synthetic_retained_denial", denial.ErrorCodes);
            Assert.Equal("{\"retained\":true}", denial.EvidenceJson);
        }
        finally
        {
            if (created)
            {
                using var poolKey = new SqliteConnection("Data Source=" + Path.Combine(folder, StorageManager.DbName));
                SqliteConnection.ClearPool(poolKey);
                if (Path.GetFullPath(folder) != expected || Path.GetFileName(folder) != scan.ToString())
                {
                    throw new InvalidOperationException("Owned random scan folder guard changed; do not delete.");
                }
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
        }
    }

    /// <summary>Creates one independently observed physical file with real identity, web/list metadata, and the original NoExecute 0/1 field rather than a full mask.</summary>
    private static SPFile Inventory(Guid scan, Guid site, Guid web, Guid list, string url, string noExecuteRaw)
    {
        var file = Guid.NewGuid();
        return new SPFile
        {
            ScanId = scan,
            SiteCollectionId = site,
            FileUniqueId = file,
            WebId = web,
            ListId = list,
            ServerRelativeUrl = url,
            ListItemId = 42,
            Length = 4096,
            WikiFieldHasValue = false,
            ItemMetadataReadState = "Available",
            ItemMetadataFieldStatesJson = "{\"WikiField\":\"value\"}",
            NoExecuteRaw = noExecuteRaw,
            NoExecuteSource = "synthetic-noexecute-source",
            NoExecuteReadState = "Available",
            ProvenanceJson = "[\"observed-before-interruption\"]",
            CoverageState = "Observed",
            ObservedAtUtc = DateTime.UtcNow
        };
    }
}
