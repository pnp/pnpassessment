﻿using EFCore.BulkExtensions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using PnP.Core;
using PnP.Core.Services;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Services;
using Serilog;

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

        internal static string DbName => "assessment.db";

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
                    PostScanStatus = SiteWebStatus.Queued,
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

                await AddHistoryRecordAsync(dbContext, scanId, Constants.EventAssessmentStatusChange, DateTime.Now, $"Set to {ScanStatus.Queued}");
                await AddHistoryRecordAsync(dbContext, scanId, Constants.EventPreAssessmentStatusChange, DateTime.Now, $"Set to {SiteWebStatus.Queued}");

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
                        await AddHistoryRecordAsync(dbContext, scanId, Constants.EventAssessmentStatusChange, DateTime.Now, $"From {scan.Status} to {ScanStatus.Finished}");
                        scan.Status = ScanStatus.Finished;
                    }

                    scan.EndDate = DateTime.Now;

                    await dbContext.SaveChangesAsync();
                    Log.Information("Database updates pushed in EndScanAsync for assessment {ScanId}", scanId);

                    // Checkpoint the database as the scan is done
                    await CheckPointDatabaseAsync(dbContext);
                }
                else
                {
                    Log.Error("No assessment row for assessment {ScanId} found to update", scanId);
                    throw new Exception($"No assessment row for assessment {scanId} found to update");
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
                    Log.Information("Setting Scan table to status {Status} for assessment {ScanId}", scanStatus, scanId);
                    if (scan.Status != scanStatus)
                    {
                        await AddHistoryRecordAsync(dbContext, scanId, Constants.EventAssessmentStatusChange, DateTime.Now, $"From {scan.Status} to {scanStatus}");
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
                    Log.Information("Database updates pushed in SetScanStatusAsync for assessment {ScanId}", scanId);
                    
                    // Checkpoint the database as the scan is done
                    await CheckPointDatabaseAsync(dbContext);
                }
                else
                {
                    Log.Error("No assessment row for assessment {ScanId} found to update", scanId);
                    throw new Exception($"No assessment row for assessment {scanId} found to update");
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
                    Log.Information("Setting Scan table to preassessmentstatus {Status} for assessment {ScanId}", preScanStatus, scanId);
                    if (scan.PreScanStatus != preScanStatus)
                    {
                        await AddHistoryRecordAsync(dbContext, scanId, Constants.EventPreAssessmentStatusChange, DateTime.Now, $"From {scan.PreScanStatus} to {preScanStatus}");
                        scan.PreScanStatus = preScanStatus;
                    }

                    await dbContext.SaveChangesAsync();
                    Log.Information("Database updates pushed in SetPreScanStatusAsync for assessment {ScanId}", scanId);
                }
                else
                {
                    Log.Error("No assessment row for assessment {ScanId} found to update", scanId);
                    throw new Exception($"No assessment row for assessment {scanId} found to update");
                }
            }
        }

        internal async Task SetPostScanStatusAsync(Guid scanId, SiteWebStatus postScanStatus)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                var scan = await dbContext.Scans.FirstOrDefaultAsync(p => p.ScanId == scanId);
                if (scan != null)
                {
                    Log.Information("Setting Scan table to postassessmentstatus {Status} for assessment {ScanId}", postScanStatus, scanId);
                    if (scan.PostScanStatus != postScanStatus)
                    {
                        await AddHistoryRecordAsync(dbContext, scanId, Constants.EventPostAssessmentStatusChange, DateTime.Now, $"From {scan.PostScanStatus} to {postScanStatus}");
                        scan.PostScanStatus = postScanStatus;
                    }

                    await dbContext.SaveChangesAsync();
                    Log.Information("Database updates pushed in SetPostScanStatusAsync for assessment {ScanId}", scanId);
                }
                else
                {
                    Log.Error("No assessment row for assessment {ScanId} found to update", scanId);
                    throw new Exception($"No assessment row for assessment {scanId} found to update");
                }
            }
        }

        internal async Task<Scan> GetTelemetryScanInformationAsync(Guid scanId)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                dbContext.ChangeTracker.AutoDetectChangesEnabled = false;
                dbContext.ChangeTracker.LazyLoadingEnabled = false;

                var scan = await dbContext.Scans.FirstOrDefaultAsync(p => p.ScanId == scanId);
                if (scan != null)
                {
                    return scan;
                }
                else
                {
                    Log.Error("No assessment row for assessment {ScanId} found ", scanId);
                    throw new Exception($"No assessment row for assessment {scanId} found");
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
                    Log.Information("Setting SiteCollection table to status Running for assessment {ScanId}, site collection {SiteCollectionUrl}", scanId, siteCollectionUrl);
                    siteToUpdate.Status = SiteWebStatus.Running;
                    siteToUpdate.StartDate = DateTime.Now;

                    await dbContext.SaveChangesAsync();
                    Log.Information("Database updates pushed in StartSiteCollectionScanAsync for assessment {ScanId}", scanId);
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
                                WebUrlAbsolute = web.WebUrl != "/" ? $"{siteCollectionUrl}{web.WebUrl}" : siteCollectionUrl,
                                Template = web.WebTemplate,
                                Status = SiteWebStatus.Queued
                            });
                        }
                    }

                    if (added)
                    {
                        await dbContext.SaveChangesAsync();
                        Log.Information("Database updates pushed in StoreWebsToScanAsync for assessment {ScanId}", scanId);

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
                            WebUrlAbsolute = web.WebUrl != "/" ? $"{siteCollectionUrl}{web.WebUrl}" : siteCollectionUrl,
                            WebUrl = web.WebUrl,
                            Template = web.WebTemplate,
                            Status = SiteWebStatus.Queued
                        });
                    }

                    await dbContext.SaveChangesAsync();
                    Log.Information("Database updates pushed in StoreWebsToScanAsync for assessment {ScanId}", scanId);

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
                    Log.Information("Setting Web table to status Running for assessment {ScanId}, web {SiteCollectionUrl}{WebUrl}", scanId, siteCollectionUrl, webUrl);
                    webToUpdate.Status = SiteWebStatus.Running;
                    webToUpdate.StartDate = DateTime.Now;

                    await dbContext.SaveChangesAsync();
                    Log.Information("Database updates pushed in StartWebScanAsync for assessment {ScanId}", scanId);
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

                    Log.Information("Setting SiteCollection table to status {Status} for assessment {ScanId}, site collection {SiteCollectionUrl}",
                        failedWeb != null ? SiteWebStatus.Failed : SiteWebStatus.Finished, scanId, siteCollectionUrl);

                    siteToUpdate.Status = failedWeb != null ? SiteWebStatus.Failed : SiteWebStatus.Finished;
                    siteToUpdate.EndDate = DateTime.Now;
                    siteToUpdate.ScanDuration = (siteToUpdate.EndDate - siteToUpdate.StartDate).Seconds;

                    await dbContext.SaveChangesAsync();
                    Log.Information("Database updates pushed in EndSiteCollectionScanAsync for assessment {ScanId}", scanId);

                }
                else
                {
                    Log.Error("No site collection row for {SiteCollectionUrl} found to update in assessment {ScanId}", siteCollectionUrl, scanId);
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
                    siteToUpdate.ScanDuration = (siteToUpdate.EndDate - siteToUpdate.StartDate).Seconds;
                    siteToUpdate.Error = GetMessageFromException(ex);
                    siteToUpdate.StackTrace = (ex != null && ex.StackTrace != null) ? ex.StackTrace : null;

                    Log.Information("Setting SiteCollections table to status Failed for assessment {ScanId}, web {SiteCollectionUrl}", scanId, siteCollectionUrl);

                    await dbContext.SaveChangesAsync();
                    Log.Information("Database updates pushed in EndSiteCollectionScanWithErrorAsync for assessment {ScanId}", scanId);

                }
                else
                {
                    Log.Error("No site collection row for {SiteCollectionUrl} found to update in assessment {ScanId}", siteCollectionUrl, scanId);
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
                    webToUpdate.ScanDuration = (webToUpdate.EndDate - webToUpdate.StartDate).Seconds;

                    Log.Information("Setting Web table to status Finished for assessment {ScanId}, web {SiteCollectionUrl}{WebUrl}", scanId, siteCollectionUrl, webUrl);

                    await dbContext.SaveChangesAsync();
                    Log.Information("Database updates pushed in EndWebScanAsync for assessment {ScanId}", scanId);

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
                    webToUpdate.ScanDuration = (webToUpdate.EndDate - webToUpdate.StartDate).Seconds;
                    webToUpdate.Error = GetMessageFromException(ex);
                    webToUpdate.StackTrace = (ex != null && ex.StackTrace != null) ? ex.StackTrace : null;

                    Log.Information("Setting Web table to status Failed for assessment {ScanId}, web {SiteCollectionUrl}{WebUrl}", scanId, siteCollectionUrl, webUrl);

                    await dbContext.SaveChangesAsync();
                    Log.Information("Database updates pushed in EndWebScanWithErrorAsync for assessment {ScanId}", scanId);

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
                dbContext.ChangeTracker.AutoDetectChangesEnabled = false;

                foreach (var site in await dbContext.SiteCollections.Where(p => p.Status == SiteWebStatus.Running).ToListAsync())
                {
                    var runningWeb = await dbContext.Webs.FirstOrDefaultAsync(p => p.ScanId == scanId && p.SiteUrl == site.SiteUrl && p.Status == SiteWebStatus.Running);
                    if (runningWeb != null)
                    {
                        Log.Information("Running web found {SiteCollectionUrl}{WebUrl} for assessment {ScanId}", site.SiteUrl, runningWeb.WebUrl, scanId);
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
                dbContext.ChangeTracker.AutoDetectChangesEnabled = false;

                var pendingWeb = await dbContext.Webs.FirstOrDefaultAsync(p => p.ScanId == scanId && p.SiteUrl == siteCollectionUrl && p.Status == SiteWebStatus.Queued);
                if (pendingWeb == null)
                {
                    Log.Information("Site collection {SiteCollectionUrl} was completely done in assessment {ScanId}", siteCollectionUrl, scanId);
                    return true;
                }

                return false;
            }
        }

        internal async Task ConsolidatedScanToEnableRestartAsync(Guid scanId)
        {
            using (var dbContext = new ScanContext(scanId))
            {

                Log.Information("Starting to consolidate assessment {ScanId} at database level", scanId);

                // Sites and webs in "running" state are reset to "queued"
                foreach (var site in await dbContext.SiteCollections.Where(p => p.Status == SiteWebStatus.Running).ToListAsync())
                {
                    site.Status = SiteWebStatus.Queued;
                    site.StartDate = DateTime.MinValue;

                    Log.Information("Consolidating assessment {ScanId}, site collection {SiteCollection}", scanId, site.SiteUrl);

                    foreach (var web in await dbContext.Webs.Where(p => p.ScanId == scanId && p.SiteUrl == site.SiteUrl && p.Status == SiteWebStatus.Running).ToListAsync())
                    {
                        web.Status = SiteWebStatus.Queued;
                        web.StartDate = DateTime.MinValue;

                        Log.Information("Consolidating assessment {ScanId}, web {SiteCollection}{Web}", scanId, site.SiteUrl, web.WebUrl);

                        // All data collected as part of a running web scan is dropped as the web scan will run again when restarted
                        await DropIncompleteWebScanDataAsync(scanId, dbContext, site, web);
                    }
                }

                // Sites and webs having an error due to "task cancellation" should be retried on a restart. This typically happens
                // when a scan was paused and pausing took to long so the cancellationtoken was "cancelled" resulting in exceptions
                // being thrown
                foreach (var site in await dbContext.SiteCollections.Where(p => p.Status == SiteWebStatus.Failed).ToListAsync())
                {
                    if (!string.IsNullOrEmpty(site.Error) && site.Error.Contains("A task was canceled", StringComparison.OrdinalIgnoreCase))
                    {
                        site.Status = SiteWebStatus.Queued;
                        site.StartDate = DateTime.MinValue;

                        Log.Information("Consolidating assessment {ScanId}, site collection {SiteCollection} which was errored due to 'A task was cancelled'", scanId, site.SiteUrl);

                        // Reset the running/failed webs so we can retry them
                        foreach (var web in await dbContext.Webs.Where(p => p.ScanId == scanId && p.SiteUrl == site.SiteUrl && 
                                                                        (p.Status == SiteWebStatus.Running || p.Status == SiteWebStatus.Failed)).ToListAsync())
                        {
                            web.Status = SiteWebStatus.Queued;
                            web.StartDate = DateTime.MinValue;

                            Log.Information("Consolidating assessment {ScanId}, web {SiteCollection}{Web} which was errored due to 'A task was cancelled'", scanId, site.SiteUrl, web.WebUrl);

                            // All data collected as part of a running web scan is dropped as the web scan will run again when restarted
                            await DropIncompleteWebScanDataAsync(scanId, dbContext, site, web);
                        }
                    }
                }

                // Persist all the changes
                await dbContext.SaveChangesAsync();
                Log.Information("Database updates pushed in ConsolidatedScanToEnableRestartAsync for assessment {ScanId}", scanId);


                Log.Information("Consolidating assessment {ScanId} at database level is done", scanId);
            }
        }

        private async Task DropIncompleteWebScanDataAsync(Guid scanId, ScanContext dbContext, SiteCollection site, Web web)
        {
            // PER SCAN COMPONENT: For each scan component implement here the method to drop incomplete web scan results
            await DropSyntexIncompleteWebScanDataAsync(scanId, dbContext, site, web);
            await DropWorkflowIncompleteWebScanDataAsync(scanId, dbContext, site, web);
            await DropClassicIncompleteWebScanDataAsync(scanId, dbContext, site, web);
            await DropInfoPathIncompleteWebScanDataAsync(scanId, dbContext, site, web);
            await DropAddInIncompleteWebScanDataAsync(scanId, dbContext, site, web);
            await DropAlertsIncompleteWebScanDataAsync(scanId, dbContext, site, web);
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
                    await AddHistoryRecordAsync(dbContext, scanId, Constants.EventAssessmentStatusChange, DateTime.Now, $"From {scan.Status} to {ScanStatus.Queued}");
                    scan.EndDate = DateTime.MinValue;
                    scan.Status = ScanStatus.Queued;

                    await dbContext.SaveChangesAsync();
                    Log.Information("Database updates pushed in RestartScanAsync for assessment {ScanId}", scanId);


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
                    Log.Error("No assessment row for assessment {ScanId} found to update", scanId);
                    throw new Exception($"No assessment row for assessment {scanId} found to update");
                }
            }
        }

        internal async Task<List<string>> SiteCollectionsToRestartScanningAsync(Guid scanId)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                dbContext.ChangeTracker.AutoDetectChangesEnabled = false;

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
                dbContext.ChangeTracker.AutoDetectChangesEnabled = false;

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
                Log.Information("Database updates pushed in StoreCacheResultsAsync for assessment {ScanId}", scanId);
            }
        }

        internal async Task<Dictionary<string, string>> LoadCacheResultsAsync(Guid scanId)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                dbContext.ChangeTracker.AutoDetectChangesEnabled = false;

                Dictionary<string, string> cacheData = new();

                int count = 0;
                foreach (var cacheEntry in await dbContext.Cache.Where(p => p.ScanId == scanId).ToListAsync())
                {
                    cacheData[cacheEntry.Key] = cacheEntry.Value;
                    count++;
                }

                Log.Information("For assessment {ScanId} {Count} items were loaded from cache", scanId, count);

                return cacheData;
            }
        }

        internal async Task<ScanResultFromDatabase> GetScanResultAsync(Guid scanId)
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
                Log.Error("Could not get results for assessment {ScanId}. Error: {Error}", scanId, ex.Message);
            }

            return null;
        }

        internal async Task CheckPointDatabaseAsync(ScanContext dbContext)
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
        
        // PER SCAN COMPONENT: storage methods per scanner
        #region Syntex
        internal async Task StoreSyntexInformationAsync(Guid scanId, List<SyntexList> syntexLists, List<SyntexContentType> syntexContentTypes, 
                                                                     List<SyntexContentTypeField> syntexContentTypeFields, List<SyntexField> syntexFields,
                                                                     List<SyntexModelUsage> syntexModelUsage, List<SyntexFileType> syntexFileTypes, List<SyntexTermSet> syntexTermSets)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                await dbContext.SyntexLists.AddRangeAsync(syntexLists.ToArray());
                await dbContext.SyntexContentTypes.AddRangeAsync(syntexContentTypes.ToArray());
                await dbContext.SyntexContentTypeFields.AddRangeAsync(syntexContentTypeFields.ToArray());
                await dbContext.SyntexFields.AddRangeAsync(syntexFields.ToArray());
                await dbContext.SyntexModelUsage.AddRangeAsync(syntexModelUsage.ToArray());
                await dbContext.SyntexFileTypes.AddRangeAsync(syntexFileTypes.ToArray());
                await dbContext.SyntexTermSets.AddRangeAsync(syntexTermSets.ToArray());

                await dbContext.SaveChangesAsync();
                Log.Information("StoreSyntexInformationAsync succeeded");
            }
        }

        internal async Task<bool> IsContentTypeStoredAsync(Guid scanId, string contentTypeId)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                dbContext.ChangeTracker.AutoDetectChangesEnabled = false;

                var contentType = await dbContext.SyntexContentTypeOverview.FirstOrDefaultAsync(p => p.ScanId == scanId && p.ContentTypeId == contentTypeId);

                if (contentType != null)
                {
                    return true;
                }
                else
                {
                    return false;
                }
            }
        }

        internal async Task AddToContentTypeSummaryAsync(Guid scanId, SyntexContentTypeSummary syntexContentTypeSummary)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                var contentType = await dbContext.SyntexContentTypeOverview.FirstOrDefaultAsync(p => p.ScanId == scanId && p.ContentTypeId == syntexContentTypeSummary.ContentTypeId);
                if (contentType == null)
                {
                    await dbContext.SyntexContentTypeOverview.AddAsync(syntexContentTypeSummary);
                    await dbContext.SaveChangesAsync();
                }
            }
        }
        #endregion

        #region Workflow
        internal async Task StoreWorkflowInformationAsync(Guid scanId, List<Workflow> workflowLists)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                await dbContext.Workflows.AddRangeAsync(workflowLists.ToArray());

                await dbContext.SaveChangesAsync();
                Log.Information("StoreWorkflowInformationAsync succeeded");
            }
        }
        #endregion

        #region InfoPath
        internal async Task StoreInfoPathInformationAsync(Guid scanId, List<ClassicInfoPath> infoPathLists)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                await dbContext.ClassicInfoPath.AddRangeAsync(infoPathLists.ToArray());

                await dbContext.SaveChangesAsync();
                Log.Information("StoreInfoPathInformationAsync succeeded");
            }
        }
        #endregion

        #region Classic
        internal async Task StorePageInformationAsync(Guid scanId, List<ClassicPage> pagesLists)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                await dbContext.ClassicPages.AddRangeAsync(pagesLists.ToArray());

                await dbContext.SaveChangesAsync();
                Log.Information("StorePageInformationAsync succeeded");
            }
        }

        internal async Task StoreWebPartInformationAsync(Guid scanId, List<ClassicWebPart> webParts)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                await dbContext.ClassicWebParts.AddRangeAsync(webParts.ToArray());

                await dbContext.SaveChangesAsync();
                Log.Information("StoreWebPartInformationAsync succeeded");
            }
        }

        internal async Task StoreClassicListInformationAsync(Guid scanId, List<ClassicList> classicLists)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                await dbContext.ClassicLists.AddRangeAsync(classicLists.ToArray());

                await dbContext.SaveChangesAsync();
                Log.Information("StoreClassicListInformationAsync succeeded");
            }
        }

        internal async Task StoreClassicUserCustomActionInformationAsync(Guid scanId, List<ClassicUserCustomAction> userCustomActionLists)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                await dbContext.ClassicUserCustomActions.AddRangeAsync(userCustomActionLists.ToArray());

                await dbContext.SaveChangesAsync();
                Log.Information("StoreClassicUserCustomActionInformationAsync succeeded");
            }
        }

        internal async Task StoreClassicExtensibilityInformationAsync(Guid scanId, List<ClassicExtensibility> classicExtensibilities)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                await dbContext.ClassicExtensibilities.AddRangeAsync(classicExtensibilities.ToArray());

                await dbContext.SaveChangesAsync();
                Log.Information("StoreClassicExtensibilityInformationAsync succeeded");
            }
        }

        internal async Task StorePageSummaryAsync(Guid scanId, string siteUrl, string webUrl, string template, PnPContext context, HashSet<string> remediationCodes, int modernPageCounter, 
                                                  int wikiPageCounter, int blogPageCounter, int webPartPageCounter, int aspxPageCounter, int publishingPageCounter)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                var webSummary = await dbContext.ClassicWebSummaries.FirstOrDefaultAsync(p => p.ScanId == scanId && p.SiteUrl == siteUrl && p.WebUrl == webUrl);

                if (webSummary == null)
                {
                    webSummary = new ClassicWebSummary
                    {
                        ScanId = scanId,
                        SiteUrl = siteUrl,
                        WebUrl = webUrl,
                        Template = template,
                        LastItemUserModifiedDate = context.Web.LastItemUserModifiedDate,                        
                    };
                    
                    await dbContext.ClassicWebSummaries.AddAsync(webSummary);
                }

                webSummary.ClassicASPXPages = aspxPageCounter;
                webSummary.ClassicBlogPages = blogPageCounter;
                webSummary.ClassicWikiPages = wikiPageCounter;
                webSummary.ClassicWebPartPages = webPartPageCounter;
                webSummary.ClassicPublishingPages = publishingPageCounter;
                webSummary.ModernPages = modernPageCounter;
                webSummary.ClassicPages = aspxPageCounter + blogPageCounter + wikiPageCounter + webPartPageCounter + publishingPageCounter;
                webSummary.AggregatedRemediationCodes = AggregateRemediationCodes(remediationCodes, webSummary);

                await dbContext.SaveChangesAsync();
                Log.Information("StorePageSummaryAsync succeeded");
            }
        }

        private static string AggregateRemediationCodes(HashSet<string> remediationCodes, ClassicWebSummary webSummary)
        {
            var split = webSummary.AggregatedRemediationCodes?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (split != null)
            {
                foreach (var code in split)
                {
                    remediationCodes.Add(code);
                }
            }

            return string.Join(",", remediationCodes);
        }

        internal async Task StoreListSummaryAsync(Guid scanId, string siteUrl, string webUrl, string template, PnPContext context, HashSet<string> remediationCodes, int modernListCounter,int classicListCounter)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                var webSummary = await dbContext.ClassicWebSummaries.FirstOrDefaultAsync(p => p.ScanId == scanId && p.SiteUrl == siteUrl && p.WebUrl == webUrl);

                if (webSummary == null)
                {
                    webSummary = new ClassicWebSummary
                    {
                        ScanId = scanId,
                        SiteUrl = siteUrl,
                        WebUrl = webUrl,
                        Template = template,
                        LastItemUserModifiedDate = context.Web.LastItemUserModifiedDate
                    };

                    await dbContext.ClassicWebSummaries.AddAsync(webSummary);
                }

                webSummary.ClassicLists = classicListCounter;
                webSummary.ModernLists = modernListCounter;
                webSummary.AggregatedRemediationCodes = AggregateRemediationCodes(remediationCodes, webSummary);

                await dbContext.SaveChangesAsync();
                Log.Information("StoreListSummaryAsync succeeded");
            }
        }

        internal async Task StoreWorkflowSummaryAsync(Guid scanId, string siteUrl, string webUrl, string template, PnPContext context, HashSet<string> remediationCodes)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                // Check if there are workflows for this web
                var workflows = await dbContext.Workflows.Where(p => p.ScanId == scanId && p.SiteUrl == siteUrl && p.WebUrl == webUrl).ToListAsync();

                var webSummary = await dbContext.ClassicWebSummaries.FirstOrDefaultAsync(p => p.ScanId == scanId && p.SiteUrl == siteUrl && p.WebUrl == webUrl);

                if (webSummary == null)
                {
                    webSummary = new ClassicWebSummary
                    {
                        ScanId = scanId,
                        SiteUrl = siteUrl,
                        WebUrl = webUrl,
                        Template = template,
                        LastItemUserModifiedDate = context.Web.LastItemUserModifiedDate,
                    };

                    await dbContext.ClassicWebSummaries.AddAsync(webSummary);
                }

                if (workflows.Count > 0)
                {
                    webSummary.ClassicWorkflows = workflows.Count;
                    webSummary.HasClassicWorkflow = true;
                    webSummary.AggregatedRemediationCodes = AggregateRemediationCodes(remediationCodes, webSummary);
                }
                else
                {
                    webSummary.HasClassicWorkflow = false;
                }

                await dbContext.SaveChangesAsync();
                Log.Information("StoreWorkflowSummaryAsync succeeded");
            }
        }

        internal async Task StoreInfoPathSummaryAsync(Guid scanId, string siteUrl, string webUrl, string template, PnPContext context, HashSet<string> remediationCodes, int infoPathForms)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                var webSummary = await dbContext.ClassicWebSummaries.FirstOrDefaultAsync(p => p.ScanId == scanId && p.SiteUrl == siteUrl && p.WebUrl == webUrl);

                if (webSummary == null)
                {
                    webSummary = new ClassicWebSummary
                    {
                        ScanId = scanId,
                        SiteUrl = siteUrl,
                        WebUrl = webUrl,
                        Template = template,
                        LastItemUserModifiedDate = context.Web.LastItemUserModifiedDate,
                    };

                    await dbContext.ClassicWebSummaries.AddAsync(webSummary);
                }

                if (infoPathForms > 0)
                {
                    webSummary.ClassicInfoPathForms = infoPathForms;
                    webSummary.HasClassicInfoPathForms = true;
                    webSummary.AggregatedRemediationCodes = AggregateRemediationCodes(remediationCodes, webSummary);
                }
                else
                {
                    webSummary.HasClassicInfoPathForms = false;
                }

                await dbContext.SaveChangesAsync();
                Log.Information("StoreWorkflowSummaryAsync succeeded");
            }
        }

        internal async Task StoreSiteSummaryAsync(Guid scanId, string siteUrl, string webUrl, string template, PnPContext context)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                // Check if there are workflows for this web
                var web = await dbContext.Webs.FirstOrDefaultAsync(p => p.ScanId == scanId && p.SiteUrl == siteUrl && p.WebUrl == webUrl);

                var webSummary = await dbContext.ClassicWebSummaries.FirstOrDefaultAsync(p => p.ScanId == scanId && p.SiteUrl == siteUrl && p.WebUrl == webUrl);

                if (webSummary == null)
                {
                    webSummary = new ClassicWebSummary
                    {
                        ScanId = scanId,
                        SiteUrl = siteUrl,
                        WebUrl = webUrl,
                        Template = template,
                        LastItemUserModifiedDate = context.Web.LastItemUserModifiedDate,
                    };

                    await dbContext.ClassicWebSummaries.AddAsync(webSummary);
                }

                var remediationCode = UpdateSiteSummaryData(web, webSummary);

                if (!string.IsNullOrEmpty(remediationCode))
                {
                    HashSet<string> remediationCodes = new()
                    {
                        remediationCode
                    };
                    webSummary.AggregatedRemediationCodes = AggregateRemediationCodes(remediationCodes, webSummary);
                }

                await dbContext.SaveChangesAsync();
                Log.Information("StoreSiteSummaryAsync succeeded");
            }
        }

        private static string UpdateSiteSummaryData(Web web, ClassicWebSummary webSummary)
        {
            var siteType = ClassicScanner.GetSiteType(web.Template);

            if (siteType == SiteType.Modern)
            {
                webSummary.IsModernSite = true;
            }
            else if (siteType == SiteType.Publishing)
            {
                webSummary.IsClassicPublishingSite = true;
                webSummary.RemediationCode = RemediationCodes.CS2.ToString();
                
            }
            else if (siteType == SiteType.Communication)
            {
                webSummary.IsModernCommunicationSite = true;
            }
            else if (siteType == SiteType.Blog)
            {
                webSummary.RemediationCode = RemediationCodes.CS1.ToString();
            }

            return webSummary.RemediationCode;
        }

        internal async Task StoreExtensibilitySummaryAsync(Guid scanId, string siteUrl, string webUrl, string template, PnPContext context, HashSet<string> remediationCodes, int extensibilityCount)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                var webSummary = await dbContext.ClassicWebSummaries.FirstOrDefaultAsync(p => p.ScanId == scanId && p.SiteUrl == siteUrl && p.WebUrl == webUrl);

                if (webSummary == null)
                {
                    webSummary = new ClassicWebSummary
                    {
                        ScanId = scanId,
                        SiteUrl = siteUrl,
                        WebUrl = webUrl,
                        Template = template,
                        LastItemUserModifiedDate = context.Web.LastItemUserModifiedDate,
                    };

                    await dbContext.ClassicWebSummaries.AddAsync(webSummary);
                }

                if (extensibilityCount > 0)
                {
                    webSummary.ClassicExtensibilities = extensibilityCount;
                    webSummary.HasClassicExtensibility = true;
                    webSummary.AggregatedRemediationCodes = AggregateRemediationCodes(remediationCodes, webSummary);
                }
                else
                {
                    webSummary.HasClassicExtensibility = false;
                }

                await dbContext.SaveChangesAsync();
                Log.Information("StoreExtensibilitySummaryAsync succeeded");
            }
        }

        #endregion

        #region Azure ACS and SharePoint Add-Ins

        internal async Task StoreAzureACSInformationAsync(Guid scanId, List<TempClassicACSPrincipalValidUntil> classicACSPrincipalValidUntils, List<TempClassicACSPrincipal> classicACSPrincipals, List<ClassicACSPrincipalSiteScopedPermissions> siteScopedPermissions, List<ClassicACSPrincipalTenantScopedPermissions> tenantScopedPermissions)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                await dbContext.TempClassicACSPrincipals.AddRangeAsync(classicACSPrincipals.ToArray());
                
                if (classicACSPrincipalValidUntils != null)
                {
                    await dbContext.TempClassicACSPrincipalValidUntils.AddRangeAsync(classicACSPrincipalValidUntils.ToArray());
                }
                
                await dbContext.ClassicACSPrincipalSiteScopedPermissions.AddRangeAsync(siteScopedPermissions.ToArray());
                
                // Tenant scoped permissions can be retrieved multiple times, ensure we're not trying to create duplicates
                foreach(var tenantScopedPermission in tenantScopedPermissions) 
                { 
                    if (await dbContext.ClassicACSPrincipalTenantScopedPermissions.FirstOrDefaultAsync(p => p.ScanId == tenantScopedPermission.ScanId &&
                                                                                                            p.AppIdentifier == tenantScopedPermission.AppIdentifier &&
                                                                                                            p.ProductFeature == tenantScopedPermission.ProductFeature &&
                                                                                                            p.Scope == tenantScopedPermission.Scope &&
                                                                                                            p.Right == tenantScopedPermission.Right &&
                                                                                                            p.ResourceId == tenantScopedPermission.ResourceId) == null)
                    {
                        await dbContext.ClassicACSPrincipalTenantScopedPermissions.AddAsync(tenantScopedPermission);
                    }
                }

                await dbContext.SaveChangesAsync();
                Log.Information("StoreAzureACSInformationAsync succeeded");
            }
        }

        internal async Task StoreSharePointAddInInformationAsync(Guid scanId, List<ClassicAddIn> classicAddIns)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                await dbContext.ClassicAddIns.AddRangeAsync(classicAddIns.ToArray());

                await dbContext.SaveChangesAsync();
                Log.Information("StoreSharePointAddInInformationAsync succeeded");
            }
        }

        internal async Task UpdateACSPrincipalInformationAsync(Guid scanId)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                // Copy over data from TempClassicACSPrincipals into ClassicACSPrincipals and ClassicACSPrincipalSites
                foreach (var tempACSPrincipal in await dbContext.TempClassicACSPrincipals.Where(p => p.ScanId == scanId).ToListAsync())
                {
                    if (await dbContext.ClassicACSPrincipals.FirstOrDefaultAsync(p => p.ScanId == scanId && p.AppIdentifier == tempACSPrincipal.AppIdentifier) == null)
                    {
                        await dbContext.ClassicACSPrincipals.AddAsync(new ClassicACSPrincipal
                        {
                            ScanId = tempACSPrincipal.ScanId,
                            AppIdentifier = tempACSPrincipal.AppIdentifier,
                            AllowAppOnly = tempACSPrincipal.AllowAppOnly,
                            AppDomains = tempACSPrincipal.AppDomains,
                            AppId = tempACSPrincipal.AppId,
                            RedirectUri = tempACSPrincipal.RedirectUri,
                            Title = tempACSPrincipal.Title,
                            ValidUntil = tempACSPrincipal.ValidUntil,
                            RemediationCode = tempACSPrincipal.RemediationCode,
                            // Will be updated later on
                            HasExpired = false,
                            HasSiteCollectionScopedPermissions = false,
                            HasTenantScopedPermissions = false,
                        });
                    }

                    // Add the site if needed
                    if (await dbContext.ClassicACSPrincipalSites.FirstOrDefaultAsync(p => p.ScanId == scanId && 
                                                                                          p.AppIdentifier == tempACSPrincipal.AppIdentifier && 
                                                                                          p.ServerRelativeUrl == tempACSPrincipal.ServerRelativeUrl) == null)
                    {
                        await dbContext.ClassicACSPrincipalSites.AddAsync(new ClassicACSPrincipalSite
                        {
                            ScanId = tempACSPrincipal.ScanId,
                            AppIdentifier = tempACSPrincipal.AppIdentifier,
                            ServerRelativeUrl = tempACSPrincipal.ServerRelativeUrl,
                        });
                    }

                    await dbContext.SaveChangesAsync();
                }


                // Copy over principal validity information from TempClassicACSPrincipalValidUntils into ClassicACSPrincipals
                foreach (var classicACSPrincipalValidUntil in await dbContext.TempClassicACSPrincipalValidUntils.Where(p => p.ScanId == scanId).OrderByDescending(p => p.ValidUntil).ToListAsync())
                {
                    foreach (var classicACSPrincipal in await dbContext.ClassicACSPrincipals.Where(p => p.ScanId == scanId && p.AppIdentifier == classicACSPrincipalValidUntil.AppIdentifier).ToListAsync())
                    {
                        if (classicACSPrincipal.ValidUntil == DateTime.MinValue && classicACSPrincipalValidUntil.ValidUntil != DateTime.MinValue)
                        {
                            classicACSPrincipal.ValidUntil = classicACSPrincipalValidUntil.ValidUntil;
                        }
                    }
                }

                // Set the calculated fields
                foreach (var classicACSPrincipal in await dbContext.ClassicACSPrincipals.Where(p => p.ScanId == scanId).ToListAsync())
                { 
                    if (string.IsNullOrEmpty(classicACSPrincipal.AppDomains) && string.IsNullOrEmpty(classicACSPrincipal.RedirectUri))
                    {
                        // Support the case where the app was created using Entra registration and then later granted permissions using appinv.aspx.
                        // This only applies to ACS principals scoped to the full tenant created by calling appinv.asxp from tenant admin center.
                        // In this case the appdomains and redirecturi are empty and we don't have an validUntil date, but we should not mark these
                        // as expired as they possibly are not yet expired.
                        if (classicACSPrincipal.ValidUntil != DateTime.MinValue)
                        {
                            classicACSPrincipal.HasExpired = classicACSPrincipal.ValidUntil < DateTime.Now;
                        }
                        else
                        {
                            classicACSPrincipal.HasExpired = false;
                        }
                    }
                    else
                    {
                        classicACSPrincipal.HasExpired = classicACSPrincipal.ValidUntil < DateTime.Now;
                    }

                    if (await dbContext.ClassicACSPrincipalTenantScopedPermissions.FirstOrDefaultAsync(p => p.ScanId == scanId && p.AppIdentifier == classicACSPrincipal.AppIdentifier) != null)
                    {
                        classicACSPrincipal.HasTenantScopedPermissions = true;
                    }

                    if (await dbContext.ClassicACSPrincipalSiteScopedPermissions.FirstOrDefaultAsync(p => p.ScanId == scanId && p.AppIdentifier == classicACSPrincipal.AppIdentifier) != null)
                    {
                        classicACSPrincipal.HasSiteCollectionScopedPermissions = true;
                    }
                }

                foreach(var classicAddIn in await dbContext.ClassicAddIns.Where(p => p.ScanId == scanId).ToListAsync())
                {
                    var usedACSPrincipal = await dbContext.ClassicACSPrincipals.FirstOrDefaultAsync(p => p.ScanId == scanId && p.AppIdentifier == classicAddIn.AppIdentifier);
                    if (usedACSPrincipal  != null && usedACSPrincipal.HasExpired) 
                    { 
                        classicAddIn.HasExpired = usedACSPrincipal.HasExpired;
                    }
                }

                await dbContext.SaveChangesAsync();
                Log.Information("UpdateACSPrincipalValidUntilInformationAsync succeeded");
            }
        }

        #endregion

        #region Alerts
        internal async Task StoreAlertsInformationAsync(Guid scanId, List<Alerts> alertsList)
        {
            using (var dbContext = new ScanContext(scanId))
            {
                await dbContext.Alerts.AddRangeAsync(alertsList.ToArray());

                await dbContext.SaveChangesAsync();
                Log.Information("StoreAlertsInformationAsync succeeded");
            }
        }
        #endregion

        // PER SCAN COMPONENT: implement DropXXXIncompleteWebScanDataAsync methods
        private async Task DropSyntexIncompleteWebScanDataAsync(Guid scanId, ScanContext dbContext, SiteCollection site, Web web)
        {
            foreach (var syntexListResult in await dbContext.SyntexLists.Where(p => p.ScanId == scanId && p.SiteUrl == site.SiteUrl && p.WebUrl == web.WebUrl).ToListAsync())
            {
                dbContext.SyntexLists.Remove(syntexListResult);
            }
            foreach (var syntexContentTypeFieldResult in await dbContext.SyntexContentTypeFields.Where(p => p.ScanId == scanId && p.SiteUrl == site.SiteUrl && p.WebUrl == web.WebUrl).ToListAsync())
            {
                dbContext.SyntexContentTypeFields.Remove(syntexContentTypeFieldResult);
            }
            foreach (var syntexContentTypeResult in await dbContext.SyntexContentTypes.Where(p => p.ScanId == scanId && p.SiteUrl == site.SiteUrl && p.WebUrl == web.WebUrl).ToListAsync())
            {
                dbContext.SyntexContentTypes.Remove(syntexContentTypeResult);
            }
            foreach (var syntexFieldResult in await dbContext.SyntexFields.Where(p => p.ScanId == scanId && p.SiteUrl == site.SiteUrl && p.WebUrl == web.WebUrl).ToListAsync())
            {
                dbContext.SyntexFields.Remove(syntexFieldResult);
            }
            foreach (var syntexModelUsage in await dbContext.SyntexModelUsage.Where(p => p.ScanId == scanId && p.SiteUrl == site.SiteUrl && p.WebUrl == web.WebUrl).ToListAsync())
            {
                dbContext.SyntexModelUsage.Remove(syntexModelUsage);
            }
            foreach (var syntexFileType in await dbContext.SyntexFileTypes.Where(p => p.ScanId == scanId && p.SiteUrl == site.SiteUrl && p.WebUrl == web.WebUrl).ToListAsync())
            {
                dbContext.SyntexFileTypes.Remove(syntexFileType);
            }
            foreach(var syntexTermSet in await dbContext.SyntexTermSets.Where(p => p.ScanId == scanId).ToListAsync())
            {
                dbContext.SyntexTermSets.Remove(syntexTermSet);
            }
            Log.Information("Consolidating assessment {ScanId}: dropping Syntex results for web {SiteCollection}{Web}", scanId, site.SiteUrl, web.WebUrl);
        }

        private async Task DropWorkflowIncompleteWebScanDataAsync(Guid scanId, ScanContext dbContext, SiteCollection site, Web web)
        {
            foreach (var workflow in await dbContext.Workflows.Where(p => p.ScanId == scanId && p.SiteUrl == site.SiteUrl && p.WebUrl == web.WebUrl).ToListAsync())
            {
                dbContext.Workflows.Remove(workflow);
            }
            Log.Information("Consolidating assessment {ScanId}: dropping Workflow results for web {SiteCollection}{Web}", scanId, site.SiteUrl, web.WebUrl);
        }

        private async Task DropInfoPathIncompleteWebScanDataAsync(Guid scanId, ScanContext dbContext, SiteCollection site, Web web)
        {
            foreach (var infoPath in await dbContext.ClassicInfoPath.Where(p => p.ScanId == scanId && p.SiteUrl == site.SiteUrl && p.WebUrl == web.WebUrl).ToListAsync())
            {
                dbContext.ClassicInfoPath.Remove(infoPath);
            }
            Log.Information("Consolidating assessment {ScanId}: dropping InfoPath results for web {SiteCollection}{Web}", scanId, site.SiteUrl, web.WebUrl);
        }

        private async Task DropClassicIncompleteWebScanDataAsync(Guid scanId, ScanContext dbContext, SiteCollection site, Web web)
        {
            foreach (var workflow in await dbContext.Workflows.Where(p => p.ScanId == scanId && p.SiteUrl == site.SiteUrl && p.WebUrl == web.WebUrl).ToListAsync())
            {
                dbContext.Workflows.Remove(workflow);
            }

            foreach (var infoPath in await dbContext.ClassicInfoPath.Where(p => p.ScanId == scanId && p.SiteUrl == site.SiteUrl && p.WebUrl == web.WebUrl).ToListAsync())
            {
                dbContext.ClassicInfoPath.Remove(infoPath);
            }

            foreach (var page in await dbContext.ClassicPages.Where(p => p.ScanId == scanId && p.SiteUrl == site.SiteUrl && p.WebUrl == web.WebUrl).ToListAsync())
            {
                dbContext.ClassicPages.Remove(page);
            }

            foreach (var webPart in await dbContext.ClassicWebParts.Where(p => p.ScanId == scanId && p.SiteUrl == site.SiteUrl && p.WebUrl == web.WebUrl).ToListAsync())
            {
                dbContext.ClassicWebParts.Remove(webPart);
            }

            foreach (var list in await dbContext.ClassicLists.Where(p => p.ScanId == scanId && p.SiteUrl == site.SiteUrl && p.WebUrl == web.WebUrl).ToListAsync())
            {
                dbContext.ClassicLists.Remove(list);
            }

            foreach (var list in await dbContext.ClassicUserCustomActions.Where(p => p.ScanId == scanId && p.SiteUrl == site.SiteUrl && p.WebUrl == web.WebUrl).ToListAsync())
            {
                dbContext.ClassicUserCustomActions.Remove(list);
            }

            foreach (var list in await dbContext.ClassicExtensibilities.Where(p => p.ScanId == scanId && p.SiteUrl == site.SiteUrl && p.WebUrl == web.WebUrl).ToListAsync())
            {
                dbContext.ClassicExtensibilities.Remove(list);
            }
            
            foreach (var list in await dbContext.ClassicWebSummaries.Where(p => p.ScanId == scanId && p.SiteUrl == site.SiteUrl && p.WebUrl == web.WebUrl).ToListAsync())
            {
                dbContext.ClassicWebSummaries.Remove(list);
            }

            foreach (var list in await dbContext.ClassicSiteSummaries.Where(p => p.ScanId == scanId && p.SiteUrl == site.SiteUrl).ToListAsync())
            {
                dbContext.ClassicSiteSummaries.Remove(list);
            }

            Log.Information("Consolidating assessment {ScanId}: dropping Classic results for web {SiteCollection}{Web}", scanId, site.SiteUrl, web.WebUrl);
        }

        private async Task DropAddInIncompleteWebScanDataAsync(Guid scanId, ScanContext dbContext, SiteCollection site, Web web)
        {
            foreach (var addIns in await dbContext.ClassicAddIns.Where(p => p.ScanId == scanId && p.SiteUrl == site.SiteUrl && p.WebUrl == web.WebUrl).ToListAsync())
            {
                dbContext.ClassicAddIns.Remove(addIns);
            }
            Log.Information("Consolidating assessment {ScanId}: dropping Add-In results for web {SiteCollection}{Web}", scanId, site.SiteUrl, web.WebUrl);
        }

        private async Task DropAlertsIncompleteWebScanDataAsync(Guid scanId, ScanContext dbContext, SiteCollection site, Web web)
        {
            foreach (var alert in await dbContext.Alerts.Where(p => p.ScanId == scanId && p.SiteUrl == site.SiteUrl && p.WebUrl == web.WebUrl).ToListAsync())
            {
                dbContext.Alerts.Remove(alert);
            }
            Log.Information("Consolidating assessment {ScanId}: dropping Alerts results for web {SiteCollection}{Web}", scanId, site.SiteUrl, web.WebUrl);
        }

#if DEBUG
        private async Task DropTestIncompleteWebScanDataAsync(Guid scanId, ScanContext dbContext, SiteCollection site, Web web)
        {
            foreach (var testResult in await dbContext.TestDelays.Where(p => p.ScanId == scanId && p.SiteUrl == site.SiteUrl && p.WebUrl == web.WebUrl).ToListAsync())
            {
                dbContext.TestDelays.Remove(testResult);
                Log.Information("Consolidating assessment {ScanId}: dropping test results for web {SiteCollection}{Web}", scanId, site.SiteUrl, web.WebUrl);
            }
        }
#endif

        #endregion

    }
}
