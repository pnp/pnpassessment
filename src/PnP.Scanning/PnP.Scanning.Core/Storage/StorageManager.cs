using EFCore.BulkExtensions;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Services;
using Serilog;

namespace PnP.Scanning.Core.Storage
{
    internal sealed class StorageManager
    {
        internal static string DbName => "scan.db";

        internal async Task LaunchNewScanAsync(Guid scanId, StartRequest start, List<string> siteCollectionList)
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
                    Version = VersionManager.GetCurrentVersion(),
                    Status = ScanStatus.Running,
                    CLIMode = start.Mode,
                    CLIEnvironment = start.Environment,
                    CLITenant = start.Tenant,
                    CLISiteList = start.SitesList,
                    CLISiteFile = start.SitesFile,
                    CLIAuthMode = start.AuthMode,
                    CLIApplicationId = start.ApplicationId,
                });

                if (start.Properties.Count > 0)
                {
                    foreach(var property in start.Properties)
                    {
                        dbContext.Properties.Add(new Property
                        {
                            ScanId = scanId,
                            Name = property.Property,
                            Type = property.Type,
                            Value = property.Value
                        });
                    }
                }

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

                    // Checkpoint the database as the scan is done
                    await CheckPointDatabaseAsync(dbContext);
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
                    var failedWeb = dbContext.Webs.FirstOrDefault(p => p.ScanId == scanId && p.SiteUrl == siteCollectionUrl && p.Status == ScanStatus.Failed);

                    siteToUpdate.Status = failedWeb != null ? ScanStatus.Failed : ScanStatus.Finished;
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

        internal async Task EndWebScanWithErrorAsync(Guid scanId, string siteCollectionUrl, string webUrl, Exception ex)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                var webToUpdate = dbContext.Webs.FirstOrDefault(p => p.ScanId == scanId && p.SiteUrl == siteCollectionUrl && p.WebUrl == webUrl);
                if (webToUpdate != null)
                {
                    webToUpdate.Status = ScanStatus.Failed;
                    webToUpdate.EndDate = DateTime.Now;
                    webToUpdate.Error = ex?.Message;
                    webToUpdate.StackTrace = (ex != null && ex.StackTrace != null) ? ex.StackTrace : null;

                    await dbContext.SaveChangesAsync();
                }
                else
                {
                    Log.Error("No web row for {SiteCollectionUrl}{WebUrl} found to update", siteCollectionUrl, webUrl);
                    throw new Exception($"No web row for {siteCollectionUrl}{webUrl} found to update");
                }
            }
        }

        internal async Task<ScanResultFromDatabase?> GetScanResultAsync(Guid scanId)
        {
            try
            {
                using (var dbContext = new ScanContext(scanId))
                {
                    var scan = await dbContext.Scans.FirstOrDefaultAsync(p => p.ScanId == scanId);

                    if (scan != null)
                    {
                        int total = 0;
                        int queued = 0;
                        int running = 0;
                        int paused = 0;
                        int finished = 0;
                        int failed = 0;

                        foreach (var site in await dbContext.SiteCollections.Where(p => p.ScanId == scanId).ToListAsync())
                        {

                            switch (site.Status)
                            {
                                case ScanStatus.Queued:
                                    total++;
                                    queued++;
                                    break;
                                case ScanStatus.Running:
                                    total++;
                                    running++;
                                    break;
                                case ScanStatus.Finished:
                                    total++;
                                    finished++;
                                    break;
                                case ScanStatus.Paused:
                                    total++;
                                    paused++;
                                    break;
                                case ScanStatus.Failed:
                                    total++;
                                    failed++;
                                    break;
                            }
                        }

                        var result = new ScanResultFromDatabase(scan.ScanId, scan.Status, total)
                        {
                            SiteCollectionsQueued = queued,
                            SiteCollectionsRunning = running,
                            SiteCollectionsPaused = paused,
                            SiteCollectionsFinished = finished,
                            SiteCollectionsFailed = failed,
                        };

                        result.StartDate = scan.StartDate.ToUniversalTime();
                        result.EndDate = scan.EndDate.ToUniversalTime();

                        return result;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Could not get results for scan {ScanId}. Error: {Error}", scanId, ex.Message);
            }

            return null;
        }

        internal async Task CheckPointDatabaseAsync(ScanContext? dbContext)
        {
            if (dbContext != null)
            {
                // Force a SQLite checkpoint to ensure all transactions are checkpointed from the wal file into the
                // the main DB file https://www.sqlite.org/pragma.html#pragma_wal_checkpoint
                await dbContext.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(RESTART);");
            }
        }

        internal static string GetScanDataFolder(Guid scanId)
        {
            return Path.Join(GetScannerFolder(), scanId.ToString());
        }

        internal static string GetScannerFolder()
        {
            return AppDomain.CurrentDomain.BaseDirectory;
        }

        #region Scanner specific operations

#if DEBUG

        internal async Task SaveTestScanResultsAsync(Guid scanId, string siteUrl, string webUrl, int delay1, int delay2, int delay3)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                dbContext.TestDelays.Add(new TestDelay
                {
                    ScanId = scanId,
                    SiteUrl = siteUrl,
                    WebUrl = webUrl,
                    Delay1 = delay1,
                    Delay2 = delay2,
                    Delay3 = delay3,
                });

                await dbContext.SaveChangesAsync();
            }
        }

#endif

        #endregion

    }
}
