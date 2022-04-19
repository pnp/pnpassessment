using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;
using PnP.Core.Services;
using PnP.Core.Services.Core;
using PnP.Scanning.Core.Authentication;
using PnP.Scanning.Core.Queues;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Storage;
using Serilog;
using System.Collections.Concurrent;

namespace PnP.Scanning.Core.Services
{
    /// <summary>
    /// Class for in memory tracking the running scans.
    /// 
    /// This class is loaded as singleton, don't use instance variables unless they're thread-safe
    /// </summary>
    internal sealed class ScanManager : IHostedService
    {
        private readonly IHostApplicationLifetime hostApplicationLifetime;
        private readonly IDataProtectionProvider dataProtectionProvider;
        private readonly IPnPContextFactory contextFactory;
        private readonly CsomEventHub eventHub;
        private readonly TelemetryManager telemetryManager;
        private object scanListLock = new();
        private readonly ConcurrentDictionary<Guid, Scan> scans = new();

        public ScanManager(IHostApplicationLifetime hostApplicationLifetime, StorageManager storageManager, SiteEnumerationManager siteEnumerationManager, TelemetryManager telemetry,
                           IDataProtectionProvider provider, IPnPContextFactory pnpContextFactory, CsomEventHub csomEventHub)
        {
            this.hostApplicationLifetime = hostApplicationLifetime;
            dataProtectionProvider = provider;
            contextFactory = pnpContextFactory;
            eventHub = csomEventHub;
            telemetryManager = telemetry;

            // Get notified whenever the scan engine is getting throttled (only applies for calls made via PnP Core SDK!)
            contextFactory.EventHub.RequestRetry = (retryEvent) =>
            {
                HandleRetryEvent(retryEvent);
            };

            // Get notified whenever a CSOM request is getting throttled
            eventHub.RequestRetry = (retryEvent) =>
            {
                HandleCsomRetryEvent(retryEvent);
            };

            // Hook the application stopping as that allows for cleanup 
            this.hostApplicationLifetime.ApplicationStopping.Register(OnStopping);
            this.hostApplicationLifetime.ApplicationStopped.Register(OnStopped);

            StorageManager = storageManager;

            SiteEnumerationManager = siteEnumerationManager;

            // Launch a thread that will mark the running scans as terminated
            Task.Run(async () => await MarkRunningScansAsTerminatedAsync());

            // Launch a thread that will monitor and update the list of scans
            Task.Run(async () => await AutoUpdateRunningScansAsync());

            // Launch a thread that once a minute will clean up finished scans from the in-memory list
            Task.Run(async () => await ClearFinishedOrPausedOrTerminatedScansFromMemoryAsync());
        }

        internal static StorageManager StorageManager { get; private set; }

        internal SiteEnumerationManager SiteEnumerationManager { get; private set; }

        internal ConcurrentDictionary<string, string> Cache { get; } = new();

        internal static int MaxParallelScans { get; private set; } = 3;

        internal static int ParallelSiteCollectionProcessingThreads { get; private set; } = 4;

        internal async Task<Guid> StartScanAsync(StartRequest start, AuthenticationManager authenticationManager, List<string> siteCollectionList)
        {
            Log.Information("Starting the assessment job");

            // Aren't we trying to start too many parallel scans?
            EnforeMaximumParallelRunningScans();

            Guid scanId = Guid.NewGuid();

            Log.Information("Assessment id is {ScanId}", scanId);

            await StorageManager.LaunchNewScanAsync(scanId, start, siteCollectionList);

            // Setup cancellation token
            CancellationTokenSource cancellationTokenSource = new();

            // Launch a queue to handle this scan
            var siteCollectionQueue = new SiteCollectionQueue(this, StorageManager, scanId, cancellationTokenSource.Token);

            // Configure threading for the site collection and web queues
            siteCollectionQueue.ConfigureParallelProcessing(start.Threads);

            // Get the scan configuration options to use
            OptionsBase options = OptionsBase.FromScannerInput(start);

            Log.Information("Add assessment request {ScanId} to in-memory list", scanId);
            var scan = new Scan(scanId, siteCollectionQueue, options, authenticationManager, cancellationTokenSource)
            {
                SiteCollectionsToScan = siteCollectionList.Count,
                Status = ScanStatus.Queued,
                FirstSiteCollection = siteCollectionList[0],
            };

            // Ensure scan is added to the in-memory list as once a site collection is enqueud it
            // starts executing and will check the in-memory list
            if (!scans.TryAdd(scanId, scan))
            {
                Log.Error("Assessment request was not added to the list of running assessments");
                throw new Exception("Assessment request was not added to the list of running assessments");
            }

            // Run possible prescanning task, use the root web of the first web in the site collection list
            var scanner = ScannerBase.NewScanner(this, StorageManager, contextFactory, eventHub, scanId, siteCollectionList[0], "/", options);
            if (scanner != null)
            {
                try
                {
                    await StorageManager.SetPreScanStatusAsync(scanId, SiteWebStatus.Running);
                    await scanner.PreScanningAsync();

                    // Persist the possibly cached data, needed to restore this data during a restart
                    await PersistCacheDataAsync(scanId);

                    await StorageManager.SetPreScanStatusAsync(scanId, SiteWebStatus.Finished);
                }
                catch (Exception ex)
                {
                    // The web scan failed, log accordingly
                    Log.Error(ex, "Preassessment for assessment {ScanId} failed. Error: {Error}", scanId, ex.Message);
                    await StorageManager.SetPreScanStatusAsync(scanId, SiteWebStatus.Failed);
                }
            }

            Log.Information("Start enqueuing {SiteCollectionCount} site collections for assessment {ScanId}", siteCollectionList.Count, scanId);
            // Enqueue the received site collections
            foreach (var site in siteCollectionList)
            {
                await siteCollectionQueue.EnqueueAsync(new SiteCollectionQueueItem(options, contextFactory, eventHub, site));
            }
            Log.Information("Enqueued {SiteCollectionCount} site collections for assessment {ScanId}", siteCollectionList.Count, scanId);

            // We're done
            Log.Information("Assessment started for {ScanId}!", scanId);

            return scanId;
        }

        private void EnforeMaximumParallelRunningScans()
        {
            if (NumberOfScansRunning() >= MaxParallelScans)
            {
                Log.Error("Max number of parallel assessments reached");
                throw new Exception("Max number of parallel assessments reached");
            }
        }

        internal async Task RestartScanAsync(Guid scanId, RestartRequest request, Action<string> feedback)
        {
            Log.Information("Restarting assessment {ScanId}", scanId);

            var listedScans = await GetScanListAsync(new ListRequest());
            var scanToRestart = listedScans.Status.FirstOrDefault(p => p.Id.Equals(scanId.ToString(), StringComparison.OrdinalIgnoreCase));
            if (scanToRestart == null)
            {
                feedback.Invoke($"Cannot restart assessment {scanId} as it's unknown");
                Log.Warning("Cannot restart assessment {ScanId} as it's unknown", scanId);
                return;
            }

            var scanStatus = (ScanStatus)System.Enum.Parse(typeof(ScanStatus), scanToRestart.Status);

            if (scanStatus == ScanStatus.Paused)
            {
                // Handle the scan restart
                await ProcessScanRestartAsync(scanId, request, (status) =>
                {
                    feedback.Invoke(status);
                });
            }
            else if (scanStatus == ScanStatus.Terminated)
            {
                // When a scan is terminated only the scan table status is set to Terminated, everything else 
                // just is what it was when the process terminated. First ensure the database is "consolidated"
                // before restarting the terminated scan
                feedback.Invoke($"Consolidating previously terminated assessment {scanId} first");
                await StorageManager.ConsolidatedScanToEnableRestartAsync(scanId);

                // Handle the scan restart
                await ProcessScanRestartAsync(scanId, request, (status) =>
                {
                    feedback.Invoke(status);
                });
            }
            else
            {
                feedback.Invoke($"Cannot restart assessment {scanId} as it's status is {scanStatus}");
                Log.Warning("Cannot restart assessment {ScanId} as it's status is {Status}", scanId, scanStatus);
                return;
            }
        }

        private async Task ProcessScanRestartAsync(Guid scanId, RestartRequest request, Action<string> feedback)
        {
            // Aren't we trying to start too many parallel scans?
            EnforeMaximumParallelRunningScans();

            // Update scan status in database
            var start = await StorageManager.RestartScanAsync(scanId);

            // Get site collections to enqueue (as they were not previously finished
            var siteCollectionList = await StorageManager.SiteCollectionsToRestartScanningAsync(scanId);

            if (siteCollectionList.Count > 0)
            {
                feedback.Invoke($"{siteCollectionList.Count} site collections will be in scope of this restart");

                // Configure auth
                var authenticationManager = AuthenticationManager.Create(start, dataProtectionProvider);

                // Populate in-memory cache again from persisted cache data
                await LoadCachedDataAsync(scanId);

                // Setup cancellation token
                CancellationTokenSource cancellationTokenSource = new();

                // Launch a queue to handle this scan
                var siteCollectionQueue = new SiteCollectionQueue(this, StorageManager, scanId, cancellationTokenSource.Token);

                // Configure threading for the site collection and web queues
                int threadsToUse = start.Threads;
                if (request.Threads > 0)
                {
                    Log.Information("Assessment {ScanId} was originally started with {Threads} but overriden to use {NewThreads} during this restart", scanId, threadsToUse, request.Threads);
                    threadsToUse = request.Threads;
                }
                else
                {
                    Log.Information("Assessments {ScanId} was originally started with {Threads}, restart will use the same setting", scanId, threadsToUse);
                }
                siteCollectionQueue.ConfigureParallelProcessing(threadsToUse);

                // Get the scan configuration options to use
                OptionsBase options = OptionsBase.FromScannerInput(start);

                var scan = new Scan(scanId, siteCollectionQueue, options, authenticationManager, cancellationTokenSource)
                {
                    SiteCollectionsToScan = siteCollectionList.Count,
                    Status = ScanStatus.Queued,
                    FirstSiteCollection = siteCollectionList[0]
                };

                // Important to add the scan to the in-memory list as after queueing a site collection
                // can start processing immediately and that requires the in-memory list entry
                if (!scans.TryAdd(scanId, scan))
                {
                    Log.Error("Assessment restart request was not added to the list of running assessments");
                    throw new Exception("Assessment restart request was not added to the list of running assessments");
                }

                Log.Information("Start enqueuing {SiteCollectionCount} site collections for restarting assessment {ScanId}", siteCollectionList.Count, scanId);
                // Enqueue the received site collections
                foreach (var site in siteCollectionList)
                {
                    await siteCollectionQueue.EnqueueAsync(new SiteCollectionQueueItem(options, contextFactory, eventHub, site) { Restart = true });
                }

                feedback.Invoke($"{siteCollectionList.Count} site collections queued for assessing again");

                // We're done
                Log.Information("Enqueued {SiteCollectionCount} site collections for restarting assessment {ScanId}", siteCollectionList.Count, scanId);
            }
            else
            {
                Log.Information("No pending site collections to be assessed when restarting assessment {ScanId}", scanId);
            }
        }

#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
        internal async Task<StatusReply> GetScanStatusAsync()
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
        {
            var statusReply = new StatusReply();

            foreach (var runningScan in scans.ToList())
            {
                statusReply.Status.Add(new ScanStatusReply
                {
                    Id = runningScan.Value.Id.ToString(),
                    Mode = runningScan.Value.Options.Mode,
                    Status = runningScan.Value.PostScanRunning ? "Finalizing" : runningScan.Value.Status.ToString(),
                    SiteCollectionsToScan = runningScan.Value.SiteCollectionsToScan,
                    SiteCollectionsScanned = runningScan.Value.SiteCollectionsScanned,
                    Duration = Duration.FromTimeSpan(TimeSpan.FromSeconds((DateTime.Now - runningScan.Value.StartedScanSessionAt).TotalSeconds)),
                    Started = Timestamp.FromDateTime(runningScan.Value.StartedScanSessionAt.ToUniversalTime()),
                    RequestsThrottled = runningScan.Value.RequestsThrottled,
                    RequestsRetriedDueToNetworkError = runningScan.Value.RequestsRetriedDueToNetworkIssues,
                    RetryingRequestAt = Timestamp.FromDateTime(runningScan.Value.RetryingRequestAt.ToUniversalTime()),
                });
            }

            return statusReply;
        }

        internal async Task<ListReply> GetScanListAsync(ListRequest request)
        {
            // When nothing was requested we default to returning all
            if (!request.Running && !request.Paused && !request.Finished && !request.Terminated)
            {
                request.Running = true;
                request.Paused = true;
                request.Finished = true;
                request.Terminated = true;
            }

            return await ScanEnumerationManager.EnumerateScansFromDiskAsync(StorageManager, request.Running, request.Paused, request.Finished, request.Terminated);
        }

        internal AuthenticationManager GetScanAuthenticationManager(Guid scanId)
        {
            if (scans.ContainsKey(scanId))
            {
                return scans[scanId].AuthenticationManager;
            }
            else
            {
                Log.Error("No authentication manager available for {ScanId}", scanId);
                throw new Exception($"No authentication manager available for {scanId}");
            }
        }

        internal CancellationTokenSource GetCancellationTokenSource(Guid scanId)
        {
            if (scans.ContainsKey(scanId))
            {
                return scans[scanId].CancellationTokenSource;
            }
            else
            {
                Log.Error("No cancellation token available for {ScanId}", scanId);
                throw new Exception($"No cancellation token available for {scanId}");
            }            
        }

        internal void CancelScan(Guid scanId, bool all)
        {
            if (all)
            {
                Log.Information("Cancelling requests for all running assessments");
                lock (scanListLock)
                {
                    foreach (var scan in scans)
                    {
                        scan.Value.CancellationTokenSource.Cancel();
                    }
                }
            }
            else
            {
                Log.Information("Cancelling requests for assessment {ScanId}", scanId);
                lock (scanListLock)
                {
                    scans[scanId].CancellationTokenSource.Cancel();
                }
            }
        }

        internal async Task SetPausingStatusAsync(Guid scanId, bool all, ScanStatus pauseMode)
        {
            List<Guid> scansToPause = new();
            if (all)
            {
                Log.Information("Setting {PauseMode} bit for all running assessments", pauseMode);
                lock (scanListLock)
                {
                    foreach (var scan in scans)
                    {
                        scan.Value.Status = pauseMode;
                        scansToPause.Add(scan.Key);
                    }
                }
            }
            else
            {
                Log.Information("Setting {PauseMode} bit for assessment {ScanId}", pauseMode, scanId);
                lock (scanListLock)
                {
                    scans[scanId].Status = pauseMode;
                    scansToPause.Add(scanId);
                }
            }

            // Update database
            foreach (var scan in scansToPause)
            {
                await StorageManager.SetScanStatusAsync(scan, pauseMode);
            }

            if (pauseMode == ScanStatus.Paused || pauseMode == ScanStatus.Terminated)
            {
                ClearFinishedOrPausedOrTerminatedScansFromMemory();
            }
        }

        internal bool IsPausing(Guid scanId)
        {
            // Import to also protect this operation via the scan list lock as otherwise it can result in deadlocks
            if (scans.ContainsKey(scanId))
            {
                return scans[scanId].Status == ScanStatus.Pausing || scans[scanId].Status == ScanStatus.Paused;
            }
            else
            {
                Log.Warning("Error while running IsPausing for assessment {ScanId}. Error = assessment if not listed yet", scanId);
                return false;
            }
        }

        internal bool ScanExists(Guid scanId)
        {
            // Ensure the in-memory table is updated to avoid adding duplicate entries
            ClearFinishedOrPausedOrTerminatedScansFromMemory();

            return scans.ContainsKey(scanId);
        }

        internal async Task PrepareDatabaseForPauseAsync(Guid scanId, bool all)
        {
            List<Guid> scansToPause = new();
            if (all)
            {
                foreach (var scan in scans.ToList())
                {
                    scansToPause.Add(scan.Key);
                }
            }
            else
            {
                scansToPause.Add(scanId);
            }

            foreach (var scan in scansToPause)
            {
                await StorageManager.ConsolidatedScanToEnableRestartAsync(scan);
            }
        }

        internal async Task<bool> WaitForPendingWebScansAsync(Guid scanId, bool all, int maxChecks = 30, int delay = 10)
        {
            bool pendingWebScans = true;
            int checksDone = 0;

            List<Guid> scansToPause = new();
            if (all)
            {
                foreach (var scan in scans.ToList())
                {
                    scansToPause.Add(scan.Key);
                }
            }
            else
            {
                scansToPause.Add(scanId);
            }

            // No point in waiting if there are no scans to inspect
            if (scansToPause.Count == 0)
            {
                return true;
            }

            do
            {
                Log.Information("Check for running web assessments for assessment {ScanId}", all ? "*ALL*" : scanId);

                foreach (var scan in scansToPause)
                {
                    // Check for pending web scans
                    pendingWebScans = await StorageManager.HasRunningWebScansAsync(scan);

                    // One of the scans being paused has pending web scans, no need to check the others 
                    if (pendingWebScans)
                    {
                        break;
                    }
                }

                if (pendingWebScans)
                {
                    Log.Information("Running web assessments for assessment {ScanId}, waiting {Delay} seconds", all ? "*ALL*" : scanId, delay);
                    checksDone++;
                    await Task.Delay(TimeSpan.FromSeconds(delay));
                }
            }
            while (pendingWebScans && checksDone <= maxChecks);

            // return false if there are still pending web scans
            return !pendingWebScans;
        }

        internal async Task UpdateScanStatusAsync(Guid scanId, ScanStatus scanStatus)
        {
            Log.Information("Updating assessment status for assessment {ScanId} to {ScanStatus}", scanId, scanStatus);

            bool databaseUpdateNeeded = false;
            lock (scanListLock)
            {
                if (scans[scanId].Status != scanStatus)
                {
                    scans[scanId].Status = scanStatus;
                    databaseUpdateNeeded = true;
                }
            }

            if (databaseUpdateNeeded)
            {
                Log.Information("Updating assessment status for assessment {ScanId} to {ScanStatus} in database", scanId, scanStatus);
                await StorageManager.SetScanStatusAsync(scanId, scanStatus);
                // Add a short delay
                await Task.Delay(TimeSpan.FromMilliseconds(500));
            }
        }

        internal void SiteCollectionScanned(Guid scanId)
        {
            lock (scanListLock)
            {
                scans[scanId].SiteCollectionWasScanned();
            }
            Log.Information("A site collection was fully assessed for assessment {ScanId}", scanId);
        }

        private void RequestWasThrottled(Guid scanId, int waitTimeInSeconds)
        {
            lock (scanListLock)
            {
                scans[scanId].RequestWasThrottled(waitTimeInSeconds);
            }
        }

        private void RequestsWasRetriedDueToNetworkIssues(Guid scanId, int waitTimeInSeconds)
        {
            lock (scanListLock)
            {
                scans[scanId].RequestsWasRetriedDueToNetworkIssues(waitTimeInSeconds);
            }
        }

        private void HandleRetryEvent(RetryEvent eventName)
        {
            // Skip the retries due to network issues (socket exceptions)
            if (eventName != null)
            {
                if (eventName.PnpContextProperties.TryGetValue(Constants.PnPContextPropertyScanId, out object scanIdObject) && Guid.TryParse(scanIdObject?.ToString(), out Guid scanId))
                {
                    if (eventName.HttpStatusCode == 429 || eventName.HttpStatusCode == 503 || eventName.HttpStatusCode == 504)
                    {
                        RequestWasThrottled(scanId, eventName.WaitTime);
                        Log.Warning("[Throttling] request {Request} for assessment {ScanId}", eventName.Request, scanId);
                        return;
                    }
                    else if (eventName.Exception != null)
                    {
                        RequestsWasRetriedDueToNetworkIssues(scanId, eventName.WaitTime);
                        Log.Warning("[Retry] request {Request} for assessment {ScanId}", eventName.Request, scanId);
                        return;
                    }
                    else
                    {
                        Log.Warning("[Retry] request {Request} for assessment {ScanId}, http status code is {StatusCode} and no exception set", eventName.Request, scanId, eventName.HttpStatusCode);
                    }
                }
             
                Log.Warning("[Retry] request {Request}, no assessment id information found!");
            }
        }

        private void HandleCsomRetryEvent(CsomRetryEvent eventName)
        {
            // Skip the retries due to network issues (socket exceptions)
            if (eventName != null)
            {
                if (eventName.HttpStatusCode == 429 || eventName.HttpStatusCode == 503)
                {
                    RequestWasThrottled(eventName.ScanId, eventName.WaitTime);
                    Log.Warning("[Throttling] CSOM request for assessment {ScanId}", eventName.ScanId);
                    return;
                }
                else if (eventName.Exception != null)
                {
                    RequestsWasRetriedDueToNetworkIssues(eventName.ScanId, eventName.WaitTime);
                    Log.Warning("[Retry] CSOM request for assessment {ScanId}", eventName.ScanId);
                    return;
                }
                else
                {
                    Log.Warning("[Retry] CSOM request for assessment {ScanId}, http status code is {StatusCode} and no exception set", eventName.ScanId, eventName.HttpStatusCode);
                }
            }
        }

        internal int NumberOfScansRunning()
        {
            int running = 0;
            foreach (var scan in scans.ToList())
            {
                if (scan.Value.Status == ScanStatus.Queued || scan.Value.Status == ScanStatus.Running)
                {
                    running++;
                }
            }

            return running;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

        private void OnStopping()
        {
            Log.Information("Kestrel is stopping");

            // Mark what's running as terminated since we're killing the server process
            MarkRunningScansAsTerminatedAsync().GetAwaiter().GetResult();
            
            Log.Warning("Running assessments marked as terminated, Kestrel can shutdown now");
        }

        private void OnStopped()
        {
            Log.Information("Kestrel stopped");
        }

        private async Task AutoUpdateRunningScansAsync()
        {
            bool busy = true;
            while (busy)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(2000));

                List<Guid> scansToMarkAsDone = new();

                foreach (var scan in scans.ToList())
                {
                    if ((scan.Value.Status == ScanStatus.Queued || scan.Value.Status == ScanStatus.Running) &&
                            scan.Value.SiteCollectionsScanned == scan.Value.SiteCollectionsToScan)
                    {
                        scansToMarkAsDone.Add(scan.Value.Id);
                    }
                }

                foreach(var scanId in scansToMarkAsDone)
                {
                    // Run post scanning step
                    var scanner = ScannerBase.NewScanner(this, StorageManager, contextFactory, eventHub, scanId, scans[scanId].FirstSiteCollection, "/", scans[scanId].Options);
                    if (scanner != null)
                    {
                        try
                        {
                            lock (scanListLock)
                            {
                                scans[scanId].PostScanRunning = true;
                            }

                            await StorageManager.SetPostScanStatusAsync(scanId, SiteWebStatus.Running);
                            await scanner.PostScanningAsync();
                            await StorageManager.SetPostScanStatusAsync(scanId, SiteWebStatus.Finished);
                        }
                        catch (Exception ex)
                        {
                            // The web scan failed, log accordingly
                            Log.Error(ex, "Post assessment task for assessment {ScanId} failed. Error: {Error}", scanId, ex.Message);
                            await StorageManager.SetPostScanStatusAsync(scanId, SiteWebStatus.Failed);
                        }
                        finally
                        {
                            lock (scanListLock)
                            {
                                scans[scanId].PostScanRunning = false;
                            }
                        }
                    }

                    await UpdateScanStatusAsync(scanId, ScanStatus.Finished);

                    await StorageManager.EndScanAsync(scanId);

                    await telemetryManager.LogScanEndAsync(scanId);
                }

            }
        }

        private async Task MarkRunningScansAsTerminatedAsync()
        {
            var listedScans = await ScanEnumerationManager.EnumerateScansFromDiskAsync(StorageManager, true, false, false, false);

            int count = 0;
            foreach(var scan in listedScans.Status)
            {
                Log.Information("Marking assessment {ScanId} as Terminated", scan.Id);
                await StorageManager.SetScanStatusAsync(Guid.Parse(scan.Id), ScanStatus.Terminated);
                count++;
            }

            Log.Information("{Count} assessments are marked as terminated", count);
        }
        
        private async Task ClearFinishedOrPausedOrTerminatedScansFromMemoryAsync()
        {
            bool busy = true;
            while (busy)
            {
                // Check runs once per minute
                await Task.Delay(TimeSpan.FromMinutes(1));

                ClearFinishedOrPausedOrTerminatedScansFromMemory();
            }
        }

        private void ClearFinishedOrPausedOrTerminatedScansFromMemory()
        {
            // Clear scan list
            foreach (var scan in scans.ToList())
            {
                if (scan.Value.Status == ScanStatus.Finished || 
                    scan.Value.Status == ScanStatus.Paused || 
                    scan.Value.Status == ScanStatus.Terminated)
                {
                    if (scans.TryRemove(scan))
                    {
                        Log.Information("Removing finished/paused/terminated assessment {ScanId} from the memory list", scan.Key);
                        
                        // Clear cached data for removed scan
                        foreach (var cacheEntry in Cache)
                        {
                            if (cacheEntry.Key.StartsWith($"{scan.Key}-"))
                            {
                                if (Cache.TryRemove(cacheEntry))
                                {
                                    Log.Information("Removing cache key {Key} for finished assessment {ScanId} from the memory list", cacheEntry.Key, scan.Key);
                                }
                                else
                                {
                                    Log.Warning("Failed removing cache key {Key} for finished assessment {ScanId} from the memory list", cacheEntry.Key, scan.Key);
                                }
                            }
                        }
                    }
                    else
                    {
                        Log.Warning("Failed removing finished assessment {ScanId} from the memory list", scan.Key);
                    }
                }
            }
        }

        private async Task PersistCacheDataAsync(Guid scanId)
        {
            Dictionary<string, string> cacheData = new();

            // Get the cache data relavent for the scan to work with
            foreach(var cacheEntry in Cache.ToList())
            {
                if (cacheEntry.Key.StartsWith($"{scanId}-"))
                {
                    cacheData[cacheEntry.Key] = cacheEntry.Value;
                }
            }

            if (cacheData.Count > 0)
            {
                Log.Information("For assessment {ScanId} {Count} cache items will be persisted", scanId, cacheData.Count);
                await StorageManager.StoreCacheResultsAsync(scanId, cacheData);
            }
        }

        private async Task LoadCachedDataAsync(Guid scanId)
        {
            var cacheData = await StorageManager.LoadCacheResultsAsync(scanId);
            if (cacheData != null && cacheData.Count > 0)
            {
                foreach (var cacheEntry in cacheData)
                {
                    if (Cache.TryAdd(cacheEntry.Key, cacheEntry.Value))
                    {
                        Log.Information("For assessment {ScanId} cache key {Key} was restored with value {Value}", scanId, cacheEntry.Key, cacheEntry.Value);
                    }
                    else
                    {
                        Log.Warning("For assessment {ScanId} cache key {Key} was not restored with value {Value}", scanId, cacheEntry.Key, cacheEntry.Value);
                    }
                }
            }
        }
    }
}
