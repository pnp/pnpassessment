using EFCore.BulkExtensions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using PnP.Core;
using PnP.Scanning.Core.Services;
using Serilog;

#nullable disable

namespace PnP.Scanning.Core.Storage
{

    /// <summary>
    /// Class handling storage of data. This class is loaded as singleton, don't use instance variables unless they're thread-safe
    /// 
    /// Note: For each new scan component work is needed here. Check the PER SCAN COMPONENT: strings to find the right places to add code
    /// </summary>
    internal sealed class StorageManager
    {
        private readonly IDataProtectionProvider dataProtectionProvider;
        private readonly IDataProtector passwordProtector;

        public StorageManager(IDataProtectionProvider provider)
        {
            dataProtectionProvider = provider;
            passwordProtector = dataProtectionProvider.CreateProtector(Constants.DataProtectorMsalCachePurpose);
        }

        internal static string DbName => "scan.db";

        internal async Task LaunchNewScanAsync(Guid scanId, StartRequest start, List<string> siteCollectionList)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                //Ensure the database is created
                await dbContext.Database.MigrateAsync();

                // Add a scan record
                dbContext.Scans.Add(new Scan
                {
                    ScanId = scanId,
                    StartDate = DateTime.Now,
                    Version = VersionManager.GetCurrentVersion(),
                    Status = ScanStatus.Queued,
                    PreScanStatus = SiteWebStatus.Queued,
                    CLIMode = start.Mode,
                    CLIEnvironment = start.Environment,
                    CLITenant = start.Tenant,
                    CLISiteList = start.SitesList,
                    CLISiteFile = start.SitesFile,
                    CLIAuthMode = start.AuthMode,
                    CLIApplicationId = start.ApplicationId,
                    CLITenantId = start.TenantId,
                    CLICertPath = start.CertPath,
                    CLICertFile = start.CertFile,
                    CLICertFilePassword = !string.IsNullOrEmpty(start.CertPassword) ? passwordProtector.Protect(start.CertPassword) : start.CertPassword,
                    CLIThreads = start.Threads,
                });

                await AddHistoryRecordAsync(dbContext, scanId, Constants.EventScanStatusChange, DateTime.Now, $"Set to {ScanStatus.Queued}");
                await AddHistoryRecordAsync(dbContext, scanId, Constants.EventPreScanStatusChange, DateTime.Now, $"Set to {SiteWebStatus.Queued}");

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

                    if (scan.Status != ScanStatus.Finished)
                    {
                        await AddHistoryRecordAsync(dbContext, scanId, Constants.EventScanStatusChange, DateTime.Now, $"From {scan.Status} to {ScanStatus.Finished}");
                        scan.Status = ScanStatus.Finished;
                    }

                    scan.EndDate = DateTime.Now;

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
                    if (scan.Status != scanStatus)
                    {
                        await AddHistoryRecordAsync(dbContext, scanId, Constants.EventScanStatusChange, DateTime.Now, $"From {scan.Status} to {scanStatus}");
                        scan.Status = scanStatus;
                    }

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

        internal async Task SetPreScanStatusAsync(Guid scanId, SiteWebStatus preScanStatus)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                var scan = await dbContext.Scans.FirstOrDefaultAsync(p => p.ScanId == scanId);
                if (scan != null)
                {
                    Log.Information("Setting Scan table to prescanstatus {Status} for scan {ScanId}", preScanStatus, scanId);
                    if (scan.PreScanStatus != preScanStatus)
                    {
                        await AddHistoryRecordAsync(dbContext, scanId, Constants.EventPreScanStatusChange, DateTime.Now, $"From {scan.PreScanStatus} to {preScanStatus}");
                        scan.PreScanStatus = preScanStatus;
                    }

                    await dbContext.SaveChangesAsync();
                    Log.Information("Database updates pushed in SetPreScanStatusAsync for scan {ScanId}", scanId);
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

        internal async Task StoreWebsToScanAsync(Guid scanId, string siteCollectionUrl, List<EnumeratedWeb> webs, bool isRestart)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                // Not using BulkInsert here as it resulted in a dead lock, the amount of webs typically will
                // be one or just a few, so there's not much added benefit

                if (isRestart)
                {
                    // When restarting a scan the needed webs might already be present, so only store them when needed

                    bool added = false;
                    foreach (var web in webs)
                    {
                        var webRecord = await dbContext.Webs.FirstOrDefaultAsync(p => p.ScanId == scanId && p.SiteUrl == siteCollectionUrl && p.WebUrl == web.WebUrl);
                        if (webRecord == null)
                        {
                            added = true;
                            dbContext.Webs.Add(new Web
                            {
                                ScanId = scanId,
                                SiteUrl = siteCollectionUrl,
                                WebUrl = web.WebUrl,
                                Template = web.WebTemplate,
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
                    foreach (var web in webs)
                    {
                        dbContext.Webs.Add(new Web
                        {
                            ScanId = scanId,
                            SiteUrl = siteCollectionUrl,
                            WebUrl = web.WebUrl,
                            Template = web.WebTemplate,
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

        internal async Task EndSiteCollectionScanWithErrorAsync(Guid scanId, string siteCollectionUrl, Exception ex)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                var siteToUpdate = await dbContext.SiteCollections.FirstOrDefaultAsync(p => p.ScanId == scanId && p.SiteUrl == siteCollectionUrl);
                if (siteToUpdate != null)
                {
                    siteToUpdate.Status = SiteWebStatus.Failed;
                    siteToUpdate.EndDate = DateTime.Now;
                    siteToUpdate.Error = GetMessageFromException(ex);
                    siteToUpdate.StackTrace = (ex != null && ex.StackTrace != null) ? ex.StackTrace : null;

                    Log.Information("Setting SiteCollections table to status Failed for scan {ScanId}, web {SiteCollectionUrl}", scanId, siteCollectionUrl);

                    await dbContext.SaveChangesAsync();
                    Log.Information("Database updates pushed in EndSiteCollectionScanWithErrorAsync for scan {ScanId}", scanId);

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
                    webToUpdate.Error = GetMessageFromException(ex);
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

        private async Task DropIncompleteWebScanDataAsync(Guid scanId, ScanContext dbContext, SiteCollection site, Web web)
        {
            // PER SCAN COMPONENT: For each scan component implement here the method to drop incomplete web scan results
#if DEBUG
            await DropTestIncompleteWebScanDataAsync(scanId, dbContext, site, web);
#endif
        }

        internal async Task<StartRequest> RestartScanAsync(Guid scanId)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                var scan = await dbContext.Scans.FirstOrDefaultAsync(p => p.ScanId == scanId);
                if (scan != null)
                {
                    await AddHistoryRecordAsync(dbContext, scanId, Constants.EventScanStatusChange, DateTime.Now, $"From {scan.Status} to {ScanStatus.Queued}");
                    scan.EndDate = DateTime.MinValue;
                    scan.Status = ScanStatus.Queued;

                    await dbContext.SaveChangesAsync();
                    Log.Information("Database updates pushed in RestartScanAsync for scan {ScanId}", scanId);


                    // Emulate the original start message as the scan might need some of the passed properties
                    StartRequest start = new()
                    {
                        Mode = scan.CLIMode.ToString(),
                        AuthMode = scan.CLIAuthMode.ToString(),
                        Tenant = scan.CLITenant,
                        ApplicationId = scan.CLIApplicationId,
                        TenantId = scan.CLITenantId,
                        Environment = scan.CLIEnvironment,
                        CertPath = scan.CLICertPath,
                        CertFile = scan.CLICertFile,
                        CertPassword = !string.IsNullOrEmpty(scan.CLICertFilePassword) ? passwordProtector.Unprotect(scan.CLICertFilePassword) : scan.CLICertFilePassword,                        
                        Threads = scan.CLIThreads
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

        internal async Task<List<EnumeratedWeb>> WebsToRestartScanningAsync(Guid scanId, string siteUrl)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                List<EnumeratedWeb> webs = new();

                foreach (var web in await dbContext.Webs.Where(p => p.ScanId == scanId && p.SiteUrl == siteUrl && p.Status == SiteWebStatus.Queued).ToListAsync())
                {
                    webs.Add(new EnumeratedWeb
                    {
                        WebUrl = web.WebUrl
                    });
                }

                return webs;
            }
        }

        internal async Task StoreCacheResultsAsync(Guid scanId, Dictionary<string, string> cacheData)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                foreach(var data in cacheData)
                {
                    dbContext.Cache.Add(new Cache
                    {
                        ScanId = scanId,
                        Key = data.Key,
                        Value = data.Value,
                    });
                }

                // Persist all the changes
                await dbContext.SaveChangesAsync();
                Log.Information("Database updates pushed in StoreCacheResultsAsync for scan {ScanId}", scanId);
            }
        }

        internal async Task<Dictionary<string, string>> LoadCacheResultsAsync(Guid scanId)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                Dictionary<string, string> cacheData = new();

                int count = 0;
                foreach (var cacheEntry in await dbContext.Cache.Where(p => p.ScanId == scanId).ToListAsync())
                {
                    cacheData[cacheEntry.Key] = cacheEntry.Value;
                    count++;
                }

                Log.Information("For scan {ScanId} {Count} items were loaded from cache", scanId, count);

                return cacheData;
            }
        }

        internal async Task<ScanResultFromDatabase?> GetScanResultAsync(Guid scanId)
        {
            try
            {
                using (var dbContext = new ScanContext(scanId))
                {
                    // Trigger the database to upgrade to the latest
                    await dbContext.Database.MigrateAsync();

                    var scan = await dbContext.Scans.FirstOrDefaultAsync(p => p.ScanId == scanId);

                    if (scan != null)
                    {
                        int total = 0;
                        int queued = 0;
                        int running = 0;
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
                                case SiteWebStatus.Failed:
                                    total++;
                                    failed++;
                                    break;
                            }
                        }

                        var result = new ScanResultFromDatabase(scan.ScanId, scan.Status, total)
                        {
                            Mode = scan.CLIMode,
                            SiteCollectionsQueued = queued,
                            SiteCollectionsRunning = running,
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

        internal async Task AddHistoryRecordAsync(ScanContext dbContext, Guid scanId, string eventName, DateTime eventDate, string eventMessage)
        {
            await dbContext.History.AddAsync(new History
            {
                ScanId = scanId,
                Event = eventName,
                EventDate = eventDate,
                Message = eventMessage
            });
        }

        internal async static Task<ScanContext> GetScanContextForDataExportAsync(Guid scanId)
        {
            var dbContext = new ScanContext(scanId);
            dbContext.ChangeTracker.AutoDetectChangesEnabled = false;
            dbContext.ChangeTracker.LazyLoadingEnabled = false;

            // Ensure the db was upgraded to the latest model
            await dbContext.Database.MigrateAsync();

            return dbContext;
        }

        internal static string GetScanDataFolder(Guid scanId)
        {
            return Path.Join(GetScannerFolder(), scanId.ToString());
        }

        internal static string GetScannerFolder()
        {
            return AppDomain.CurrentDomain.BaseDirectory;
        }

        private static string GetMessageFromException(Exception ex)
        {
            string message = ex.Message;

            if (ex is PnPException pnPException)
            {
                message += $": {pnPException.Error}";
            }
            
            if (ex.InnerException != null)
            {
                message += ex.InnerException.Message;
            }

            return message;
        }

        #region Scanner specific operations

        // PER SCAN COMPONENT: implement respective SaveXXXScanResultsAsync and DropXXXIncompleteWebScanDataAsync methods

#if DEBUG

        internal async Task SaveTestScanResultsAsync(Guid scanId, string siteUrl, string webUrl, int delay1, int delay2, int delay3, string webIdString)
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
                    WebIdString = webIdString
                });

                await dbContext.SaveChangesAsync();
                Log.Information("Database updates pushed in SaveTestScanResultsAsync for scan {ScanId}", scanId);

            }
        }

        private async Task DropTestIncompleteWebScanDataAsync(Guid scanId, ScanContext dbContext, SiteCollection site, Web web)
        {
            foreach (var testResult in await dbContext.TestDelays.Where(p => p.ScanId == scanId && p.SiteUrl == site.SiteUrl && p.WebUrl == web.WebUrl).ToListAsync())
            {
                dbContext.TestDelays.Remove(testResult);
                Log.Information("Consolidating scan {ScanId}: dropping test results for web {SiteCollection}{Web}", scanId, site.SiteUrl, web.WebUrl);
            }
        }

#endif

        #endregion

    }
}
