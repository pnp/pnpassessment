using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Discovery.M1;
using PnP.Scanning.Core.Storage;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery.M1;

public sealed class ClassicAspxConsolidationRetentionTests
{
    /// <summary>
    /// Exercises the supported restart lifecycle: pending legacy results are cleared while normalized pairs and raw denial evidence survive.
    /// </summary>
    [Fact]
    public async Task ActualRestartConsolidationClearsPendingLegacyResultsButRetainsNormalizedAndRawFacts()
    {
        Guid scan = Guid.NewGuid(), site = Guid.NewGuid(), pendingWeb = Guid.NewGuid(), finishedWeb = Guid.NewGuid(), list = Guid.NewGuid(), file = Guid.NewGuid();
        const string url = "https://example.invalid/sites/retention";
        string folder = Path.GetFullPath(StorageManager.GetScanDataFolder(scan));
        string expected = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, scan.ToString()));
        Assert.Equal(expected, folder);
        Assert.Equal(scan.ToString(), Path.GetFileName(folder));
        Assert.False(Directory.Exists(folder)); // Never touch a pre-existing scan folder.
        bool created = false;
        try
        {
            Directory.CreateDirectory(folder); created = true;
            using (var db = new ScanContext(scan))
            {
                await db.Database.MigrateAsync();
                db.SiteCollections.Add(new SiteCollection { ScanId = scan, SiteUrl = url, Status = SiteWebStatus.Running, StartDate = DateTime.UtcNow });
                db.Webs.AddRange(new Web { ScanId = scan, SiteUrl = url, WebUrl = "/", WebUrlAbsolute = url, Status = SiteWebStatus.Running, StartDate = DateTime.UtcNow },
                    new Web { ScanId = scan, SiteUrl = url, WebUrl = "/finished", WebUrlAbsolute = url + "/finished", Status = SiteWebStatus.Finished });
                db.ClassicPages.AddRange(new ClassicPage { ScanId = scan, SiteUrl = url, WebUrl = "/", PageUrl = url + "/pending.aspx", PageName = "pending.aspx", ListId = list },
                    new ClassicPage { ScanId = scan, SiteUrl = url, WebUrl = "/finished", PageUrl = url + "/finished/kept.aspx", PageName = "kept.aspx", ListId = list });
                db.ClassicPageWebParts.Add(new ClassicPageWebPart { ScanId = scan, SiteUrl = url, WebUrl = "/", PageUrl = url + "/pending.aspx", WebPartIndex = 0 });
                db.ClassicLists.Add(new ClassicList { ScanId = scan, SiteUrl = url, WebUrl = "/", ListId = list });
                await db.SaveChangesAsync();
            }
            string key = ClassicAspxInventory.CreateTenantFileKey(site, file);
            var projection = new ClassicAspxProjectionWriter(scan);
            await projection.WriteAsync(new[] { new ClassicAspxInventory { ScanId = scan, AspxKey = key, SiteCollectionId = site, FileUniqueId = file,
                Url = url + "/renderer.aspx", ProvenanceJson = "[\"observed-before-interruption\"]", CoverageState = "Observed", ObservedAtUtc = DateTime.UtcNow } },
                new[] { Pair(scan, key, site, pendingWeb, list, 1), Pair(scan, key, site, finishedWeb, list, 2) });
            var raw = new AssessmentDiscoveryWriter(scan);
            await raw.WriteAsync(new[] { new ClassicPageDiscovery { ScanId = scan, SiteUrl = url, WebUrl = "/", RecordKey = "raw-pending-denied",
                RowType = "Scope", ScopeType = "Surface", DiscoveryStatus = "Denied", ErrorCodes = "synthetic_retained_denial", EvidenceJson = "{\"retained\":true}", ObservedAtUtc = DateTime.UtcNow } });

            // Real supported product lifecycle entrypoint. No reflection/private Drop* invocation or test-only runner.
            var storage = new StorageManager(new EphemeralDataProtectionProvider());
            await storage.ConsolidatedScanToEnableRestartAsync(scan);

            using (var db = new ScanContext(scan))
            {
                Assert.Equal(SiteWebStatus.Queued, (await db.SiteCollections.SingleAsync()).Status);
                Assert.Equal(SiteWebStatus.Queued, (await db.Webs.SingleAsync(x => x.WebUrl == "/")).Status);
                Assert.Equal(DateTime.MinValue, (await db.Webs.SingleAsync(x => x.WebUrl == "/")).StartDate);
                Assert.Equal(SiteWebStatus.Finished, (await db.Webs.SingleAsync(x => x.WebUrl == "/finished")).Status);
                Assert.Empty(await db.ClassicPages.Where(x => x.WebUrl == "/").ToArrayAsync());
                Assert.Empty(await db.ClassicPageWebParts.ToArrayAsync()); Assert.Empty(await db.ClassicLists.ToArrayAsync());
                Assert.Single(await db.ClassicPages.Where(x => x.WebUrl == "/finished").ToArrayAsync());
                Assert.Equal(1, await db.ClassicAspxInventories.CountAsync());
                Assert.Equal(2, await db.ClassicAspxItemBindings.CountAsync());
                Assert.Equal("Denied", (await db.ClassicPageDiscoveries.SingleAsync(x => x.RecordKey == "raw-pending-denied")).DiscoveryStatus);
                Assert.Equal("synthetic_retained_denial", (await db.ClassicPageDiscoveries.SingleAsync(x => x.RecordKey == "raw-pending-denied")).ErrorCodes);
            }
        }
        finally
        {
            if (created)
            {
                // Clear only the pool for our own exact random database, never global pools/other tests.
                using var poolKey = new SqliteConnection("Data Source=" + Path.Combine(folder, StorageManager.DbName));
                SqliteConnection.ClearPool(poolKey);
                if (Path.GetFullPath(folder) != expected || Path.GetFileName(folder) != scan.ToString())
                    throw new InvalidOperationException("Owned random scan folder guard changed; do not delete.");
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
        }
    }

    /// <summary>
    /// Creates an observed renderer binding with the exact item scope needed to distinguish pending and finished webs.
    /// </summary>
    private static ClassicAspxItemBinding Pair(Guid scan, string key, Guid site, Guid web, Guid list, int id) => new()
    {
        ScanId = scan, AspxKey = key, SiteCollectionId = site, WebId = web, ListId = list, ItemId = id,
        BindingKinds = ClassicAspxBindingKinds.ListFormRenderer, BindingProvenanceJson = "[\"observed-item\"]", ObservedAtUtc = DateTime.UtcNow
    };
}
