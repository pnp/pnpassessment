using EFCore.BulkExtensions;
using Serilog;

namespace PnP.Scanning.Core.Storage
{
    internal sealed class StorageManager
    {
        internal async Task LaunchNewScanAsync(Guid scanId, List<string> siteCollectionList)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                //Ensure the database is created
                dbContext.Database.EnsureCreated();

                // Add a scan record
                dbContext.Scans.Add(new Scan 
                { 
                    ScanId = scanId, 
                    StartDate = DateTime.Now, 
                    Status = ScanStatus.Running 
                });

                await dbContext.SaveChangesAsync();

                // Perform bulk insert to increase performance
                var siteCollectionsToAdd = new List<SiteCollection>();

                // Persist the site collections to scan
                foreach (var site in siteCollectionList)
                {
                    siteCollectionsToAdd.Add(new SiteCollection
                    {
                        ScanId = scanId,
                        SiteUrl = site,
                        Status = ScanStatus.Queued,
                    });
                }

                await dbContext.BulkInsertAsync(siteCollectionsToAdd);
            }
        }

        internal async Task EndScanAsync(Guid scanId)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                var scan = dbContext.Scans.FirstOrDefault(p => p.ScanId == scanId);
                if (scan != null)
                {
                    scan.EndDate = DateTime.Now;
                    scan.Status = ScanStatus.Finished;

                    await dbContext.SaveChangesAsync();
                }
                else
                {
                    Log.Error("No scan row for scan {ScanId} found to update", scanId);
                    throw new Exception($"No scan row for scan {scanId} found to update");
                }
            }
        }

        internal async Task StartSiteCollectionScanAsync(Guid scanId, string siteCollectionUrl)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                var siteToUpdate = dbContext.SiteCollections.FirstOrDefault(p=>p.ScanId == scanId && p.SiteUrl == siteCollectionUrl);
                if (siteToUpdate != null)
                {
                    siteToUpdate.Status = ScanStatus.Running;
                    siteToUpdate.StartDate = DateTime.Now;

                    await dbContext.SaveChangesAsync();
                }
                else
                {
                    Log.Error("No site collection row for {SiteCollectionUrl} found to update", siteCollectionUrl);
                    throw new Exception($"No site collection row for {siteCollectionUrl} found to update");
                }
            }
        }

        internal async Task StoreWebsToScanAsync(Guid scanId, string siteCollectionUrl, List<string> webs)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                // Not using BulkInsert here as it resulted in a dead lock, the amount of webs typically will
                // be one or just a few, so there's not much added benefit

                foreach (var webUrl in webs)
                {                    
                    dbContext.Webs.Add(new Web
                    {
                        ScanId = scanId,
                        SiteUrl = siteCollectionUrl,
                        WebUrl = webUrl,
                        Status = ScanStatus.Queued
                    });                    
                }
                
                await dbContext.SaveChangesAsync();
            }
        }

        internal async Task StartWebScanAsync(Guid scanId, string siteCollectionUrl, string webUrl)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                var webToUpdate = dbContext.Webs.FirstOrDefault(p => p.ScanId == scanId && p.SiteUrl == siteCollectionUrl && p.WebUrl == webUrl);
                if (webToUpdate != null)
                {
                    webToUpdate.Status = ScanStatus.Running;
                    webToUpdate.StartDate = DateTime.Now;

                    await dbContext.SaveChangesAsync();
                }
                else
                {
                    Log.Error("No web row for {SiteCollectionUrl}{WebUrl} found to update", siteCollectionUrl, webUrl);
                    throw new Exception($"No web row for {siteCollectionUrl}{webUrl} found to update");
                }
            }
        }

        internal async Task EndSiteCollectionScanAsync(Guid scanId, string siteCollectionUrl)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                var siteToUpdate = dbContext.SiteCollections.FirstOrDefault(p => p.ScanId == scanId && p.SiteUrl == siteCollectionUrl);
                if (siteToUpdate != null)
                {
                    siteToUpdate.Status = ScanStatus.Finished;
                    siteToUpdate.EndDate = DateTime.Now;

                    await dbContext.SaveChangesAsync();
                }
                else
                {
                    Log.Error("No site collection row for {SiteCollectionUrl} found to update", siteCollectionUrl);
                    throw new Exception($"No site collection row for {siteCollectionUrl} found to update");
                }
            }
        }

        internal async Task EndWebScanAsync(Guid scanId, string siteCollectionUrl, string webUrl)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                var webToUpdate = dbContext.Webs.FirstOrDefault(p => p.ScanId == scanId && p.SiteUrl == siteCollectionUrl && p.WebUrl == webUrl);
                if (webToUpdate != null)
                {
                    webToUpdate.Status = ScanStatus.Finished;
                    webToUpdate.EndDate = DateTime.Now;

                    await dbContext.SaveChangesAsync();
                }
                else
                {
                    Log.Error("No web row for {SiteCollectionUrl}{WebUrl} found to update", siteCollectionUrl, webUrl);
                    throw new Exception($"No web row for {siteCollectionUrl}{webUrl} found to update");
                }
            }
        }

        internal static string GetScanDataFolder(Guid scanId)
        {
            return Path.Join(AppDomain.CurrentDomain.BaseDirectory, scanId.ToString());
        }

    }
}
