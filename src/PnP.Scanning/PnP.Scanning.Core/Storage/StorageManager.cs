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
                    Status = ScanStatus.Queued,
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
                    foreach (var property in start.Properties)
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
                        Status = SiteWebStatus.Queued,
                    });
                }

                await dbContext.BulkInsertAsync(siteCollectionsToAdd);
            }
        }

        internal async Task EndScanAsync(Guid scanId)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                var scan = await dbContext.Scans.FirstOrDefaultAsync(p => p.ScanId == scanId);
                if (scan != null)
                {
                    scan.EndDate = DateTime.Now;
                    scan.Status = ScanStatus.Finished;

                    await dbContext.SaveChangesAsync();
                    Log.Information("Database updates pushed in EndScanAsync for scan {ScanId}", scanId);

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

        internal async Task SetScanStatusAsync(Guid scanId, ScanStatus scanStatus)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                var scan = await dbContext.Scans.FirstOrDefaultAsync(p => p.ScanId == scanId);
                if (scan != null)
                {
                    Log.Information("Setting Scan table to status {Status} for scan {ScanId}", scanStatus, scanId);
                    scan.Status = scanStatus;

                    // Consider a scan marked as Finished, Paused or Terminate to have an end date set
                    if (scanStatus != ScanStatus.Running &&
                        scanStatus != ScanStatus.Queued &&
                        scanStatus != ScanStatus.Pausing)
                    {
                        scan.EndDate = DateTime.Now;
                    }

                    await dbContext.SaveChangesAsync();
                    Log.Information("Database updates pushed in SetScanStatusAsync for scan {ScanId}", scanId);


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

                var siteToUpdate = await dbContext.SiteCollections.FirstOrDefaultAsync(p => p.ScanId == scanId && p.SiteUrl == siteCollectionUrl);
                if (siteToUpdate != null)
                {
                    Log.Information("Setting SiteCollection table to status Running for scan {ScanId}, site collection {SiteCollectionUrl}", scanId, siteCollectionUrl);
                    siteToUpdate.Status = SiteWebStatus.Running;
                    siteToUpdate.StartDate = DateTime.Now;

                    await dbContext.SaveChangesAsync();
                    Log.Information("Database updates pushed in StartSiteCollectionScanAsync for scan {ScanId}", scanId);
                }
                else
                {
                    Log.Error("No site collection row for {SiteCollectionUrl} found to update", siteCollectionUrl);
                    throw new Exception($"No site collection row for {siteCollectionUrl} found to update");
                }
            }
        }

        internal async Task StoreWebsToScanAsync(Guid scanId, string siteCollectionUrl, List<string> webs, bool isRestart)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                // Not using BulkInsert here as it resulted in a dead lock, the amount of webs typically will
                // be one or just a few, so there's not much added benefit

                if (isRestart)
                {
                    // When restarting a scan the needed webs might already be present, so only store them when needed

                    bool added = false;
                    foreach (var webUrl in webs)
                    {
                        var webRecord = await dbContext.Webs.FirstOrDefaultAsync(p => p.ScanId == scanId && p.SiteUrl == siteCollectionUrl && p.WebUrl == webUrl);
                        if (webRecord == null)
                        {
                            added = true;
                            dbContext.Webs.Add(new Web
                            {
                                ScanId = scanId,
                                SiteUrl = siteCollectionUrl,
                                WebUrl = webUrl,
                                Status = SiteWebStatus.Queued
                            });
                        }
                    }

                    if (added)
                    {
                        await dbContext.SaveChangesAsync();
                        Log.Information("Database updates pushed in StoreWebsToScanAsync for scan {ScanId}", scanId);

                    }
                }
                else
                {
                    foreach (var webUrl in webs)
                    {
                        dbContext.Webs.Add(new Web
                        {
                            ScanId = scanId,
                            SiteUrl = siteCollectionUrl,
                            WebUrl = webUrl,
                            Status = SiteWebStatus.Queued
                        });
                    }

                    await dbContext.SaveChangesAsync();
                    Log.Information("Database updates pushed in StoreWebsToScanAsync for scan {ScanId}", scanId);

                }
            }
        }

        internal async Task StartWebScanAsync(Guid scanId, string siteCollectionUrl, string webUrl)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                var webToUpdate = await dbContext.Webs.FirstOrDefaultAsync(p => p.ScanId == scanId && p.SiteUrl == siteCollectionUrl && p.WebUrl == webUrl);
                if (webToUpdate != null)
                {
                    Log.Information("Setting Web table to status Running for scan {ScanId}, web {SiteCollectionUrl}{WebUrl}", scanId, siteCollectionUrl, webUrl);
                    webToUpdate.Status = SiteWebStatus.Running;
                    webToUpdate.StartDate = DateTime.Now;

                    await dbContext.SaveChangesAsync();
                    Log.Information("Database updates pushed in StartWebScanAsync for scan {ScanId}", scanId);
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
                var siteToUpdate = await dbContext.SiteCollections.FirstOrDefaultAsync(p => p.ScanId == scanId && p.SiteUrl == siteCollectionUrl);
                if (siteToUpdate != null)
                {
                    var failedWeb = await dbContext.Webs.FirstOrDefaultAsync(p => p.ScanId == scanId && p.SiteUrl == siteCollectionUrl && p.Status == SiteWebStatus.Failed);

                    Log.Information("Setting SiteCollection table to status {Status} for scan {ScanId}, site collection {SiteCollectionUrl}",
                        failedWeb != null ? SiteWebStatus.Failed : SiteWebStatus.Finished, scanId, siteCollectionUrl);

                    siteToUpdate.Status = failedWeb != null ? SiteWebStatus.Failed : SiteWebStatus.Finished;
                    siteToUpdate.EndDate = DateTime.Now;

                    await dbContext.SaveChangesAsync();
                    Log.Information("Database updates pushed in EndSiteCollectionScanAsync for scan {ScanId}", scanId);

                }
                else
                {
                    Log.Error("No site collection row for {SiteCollectionUrl} found to update in scan {ScanId}", siteCollectionUrl, scanId);
                    throw new Exception($"No site collection row for {siteCollectionUrl} found to update");
                }
            }
        }

        internal async Task EndWebScanAsync(Guid scanId, string siteCollectionUrl, string webUrl)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                var webToUpdate = await dbContext.Webs.FirstOrDefaultAsync(p => p.ScanId == scanId && p.SiteUrl == siteCollectionUrl && p.WebUrl == webUrl);
                if (webToUpdate != null)
                {
                    webToUpdate.Status = SiteWebStatus.Finished;
                    webToUpdate.EndDate = DateTime.Now;

                    Log.Information("Setting Web table to status Finished for scan {ScanId}, web {SiteCollectionUrl}{WebUrl}", scanId, siteCollectionUrl, webUrl);

                    await dbContext.SaveChangesAsync();
                    Log.Information("Database updates pushed in EndWebScanAsync for scan {ScanId}", scanId);

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
                var webToUpdate = await dbContext.Webs.FirstOrDefaultAsync(p => p.ScanId == scanId && p.SiteUrl == siteCollectionUrl && p.WebUrl == webUrl);
                if (webToUpdate != null)
                {
                    webToUpdate.Status = SiteWebStatus.Failed;
                    webToUpdate.EndDate = DateTime.Now;
                    webToUpdate.Error = ex?.Message;
                    webToUpdate.StackTrace = (ex != null && ex.StackTrace != null) ? ex.StackTrace : null;

                    Log.Information("Setting Web table to status Failed for scan {ScanId}, web {SiteCollectionUrl}{WebUrl}", scanId, siteCollectionUrl, webUrl);

                    await dbContext.SaveChangesAsync();
                    Log.Information("Database updates pushed in EndWebScanWithErrorAsync for scan {ScanId}", scanId);

                }
                else
                {
                    Log.Error("No web row for {SiteCollectionUrl}{WebUrl} found to update", siteCollectionUrl, webUrl);
                    throw new Exception($"No web row for {siteCollectionUrl}{webUrl} found to update");
                }
            }
        }

        internal async Task<bool> HasRunningWebScansAsync(Guid scanId)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                foreach (var site in await dbContext.SiteCollections.Where(p => p.Status == SiteWebStatus.Running).ToListAsync())
                {
                    var runningWeb = await dbContext.Webs.FirstOrDefaultAsync(p => p.ScanId == scanId && p.SiteUrl == site.SiteUrl && p.Status == SiteWebStatus.Running);
                    if (runningWeb != null)
                    {
                        Log.Information("Running web found {SiteCollectionUrl}{WebUrl} for scan {ScanId}", site.SiteUrl, runningWeb.WebUrl, scanId);
                        return true;
                    }
                }

                return false;
            }
        }

        internal async Task<bool> SiteCollectionWasCompletelyHandledAsync(Guid scanId, string siteCollectionUrl)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                var pendingWeb = await dbContext.Webs.FirstOrDefaultAsync(p => p.ScanId == scanId && p.SiteUrl == siteCollectionUrl && p.Status == SiteWebStatus.Queued);
                if (pendingWeb == null)
                {
                    Log.Information("Site collection {SiteCollectionUrl} was completely done in scan {ScanId}", siteCollectionUrl, scanId);
                    return true;
                }

                return false;
            }
        }

        internal async Task ConsolidatedScanToEnableRestartAsync(Guid scanId)
        {
            using (var dbContext = new ScanContext(scanId))
            {

                Log.Information("Starting to consolidate scan {ScanId} at database level", scanId);

                // Sites and webs in "running" state are reset to "queued"
                foreach (var site in await dbContext.SiteCollections.Where(p => p.Status == SiteWebStatus.Running).ToListAsync())
                {
                    site.Status = SiteWebStatus.Queued;
                    site.StartDate = DateTime.MinValue;

                    Log.Information("Consolidating scan {ScanId}, site collection {SiteCollection}", scanId, site.SiteUrl);

                    foreach (var web in await dbContext.Webs.Where(p => p.ScanId == scanId && p.SiteUrl == site.SiteUrl && p.Status == SiteWebStatus.Running).ToListAsync())
                    {
                        web.Status = SiteWebStatus.Queued;
                        web.StartDate = DateTime.MinValue;

                        Log.Information("Consolidating scan {ScanId}, web {SiteCollection}{Web}", scanId, site.SiteUrl, web.WebUrl);

                        // All data collected as part of a running web scan is dropped as the web scan will run again when restarted
                        await DropIncompleteWebScanDataAsync(scanId, dbContext, site, web);
                    }
                }

                // Persist all the changes
                await dbContext.SaveChangesAsync();
                Log.Information("Database updates pushed in ConsolidatedScanToEnableRestartAsync for scan {ScanId}", scanId);


                Log.Information("Consolidating scan {ScanId} at database level is done", scanId);
            }
        }

        private static async Task DropIncompleteWebScanDataAsync(Guid scanId, ScanContext dbContext, SiteCollection site, Web web)
        {
#if DEBUG
            foreach (var testResult in await dbContext.TestDelays.Where(p => p.ScanId == scanId && p.SiteUrl == site.SiteUrl && p.WebUrl == web.WebUrl).ToListAsync())
            {
                dbContext.TestDelays.Remove(testResult);
                Log.Information("Consolidating scan {ScanId}: dropping test results for web {SiteCollection}{Web}", scanId, site.SiteUrl, web.WebUrl);
            }
#endif
        }

        internal async Task<StartRequest> RestartScanAsync(Guid scanId)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                var scan = await dbContext.Scans.FirstOrDefaultAsync(p => p.ScanId == scanId);
                if (scan != null)
                {
                    scan.StartDate = DateTime.MinValue;
                    scan.EndDate = DateTime.MinValue;
                    scan.Status = ScanStatus.Queued;

                    await dbContext.SaveChangesAsync();
                    Log.Information("Database updates pushed in RestartScanAsync for scan {ScanId}", scanId);


                    // Emulate the original start message as the scan might need some of the passed properties
                    StartRequest start = new() 
                    {
                        Mode = scan.CLIMode.ToString()
                    };

                    foreach(var property in await dbContext.Properties.Where(p=>p.ScanId == scanId).ToListAsync())
                    {
                        start.Properties.Add(new PropertyRequest
                        {
                            Property = property.Name,
                            Type = property.Type,
                            Value = property.Value
                        });
                    }

                    return start;
                }
                else
                {
                    Log.Error("No scan row for scan {ScanId} found to update", scanId);
                    throw new Exception($"No scan row for scan {scanId} found to update");
                }
            }
        }

        internal async Task<List<string>> SiteCollectionsToRestartScanningAsync(Guid scanId)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                List<string> siteCollections = new();

                foreach (var site in await dbContext.SiteCollections.Where(p => p.ScanId == scanId && p.Status == SiteWebStatus.Queued).ToListAsync())
                {
                    siteCollections.Add(site.SiteUrl);
                }

                return siteCollections;
            }
        }

        internal async Task<List<string>> WebsToRestartScanningAsync(Guid scanId, string siteUrl)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                List<string> webs = new();

                foreach (var web in await dbContext.Webs.Where(p => p.ScanId == scanId && p.SiteUrl == siteUrl && p.Status == SiteWebStatus.Queued).ToListAsync())
                {
                    webs.Add(web.WebUrl);
                }

                return webs;
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
                                case SiteWebStatus.Queued:
                                    total++;
                                    queued++;
                                    break;
                                case SiteWebStatus.Running:
                                    total++;
                                    running++;
                                    break;
                                case SiteWebStatus.Finished:
                                    total++;
                                    finished++;
                                    break;
                                case SiteWebStatus.Paused:
                                    total++;
                                    paused++;
                                    break;
                                case SiteWebStatus.Failed:
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
                Log.Information("Checkpointing database");
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
                Log.Information("Database updates pushed in SaveTestScanResultsAsync for scan {ScanId}", scanId);

            }
        }

#endif

        #endregion

    }
}
