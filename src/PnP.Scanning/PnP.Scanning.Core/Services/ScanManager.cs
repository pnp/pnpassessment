using Microsoft.Extensions.Hosting;
using PnP.Scanning.Core.Queues;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Storage;
using Serilog;
using System.Collections.Concurrent;

namespace PnP.Scanning.Core.Services
{
    // Class for in memory tracking the running scans
    internal sealed class ScanManager : IHostedService
    {
        private readonly IHostApplicationLifetime hostApplicationLifetime;
        private object scanListLock = new object();
        private readonly ConcurrentDictionary<Guid, Scan> scans = new();

        public ScanManager(IHostApplicationLifetime hostApplicationLifetime, StorageManager storageManager, SiteEnumerationManager siteEnumerationManager)
        {
            this.hostApplicationLifetime = hostApplicationLifetime;
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
            Task.Run(async () => await ClearFinishedOrPausedScansFromMemoryAsync());
        }

        internal StorageManager StorageManager { get; private set; }

        internal SiteEnumerationManager SiteEnumerationManager { get; private set; }

        internal int MaxParallelScans { get; private set; } = 3;

        internal int ParallelSiteCollectionProcessingThreads { get; private set; } = 4;

        internal async Task<Guid> StartScanAsync(StartRequest start, List<string> siteCollectionList)
        {
            Log.Information("Starting the scan job");

            // Aren't we trying to start too many parallel scans?
            EnforeMaximumParallelRunningScans();

            Guid scanId = Guid.NewGuid();

            Log.Information("Scan id is {ScanId}", scanId);

            await StorageManager.LaunchNewScanAsync(scanId, start, siteCollectionList);

            // Launch a queue to handle this scan
            var siteCollectionQueue = new SiteCollectionQueue(this, StorageManager, scanId);

            // Configure the queue
            siteCollectionQueue.ConfigureQueue(ParallelSiteCollectionProcessingThreads);

            // Get the scan configuration options to use
            OptionsBase options = OptionsBase.FromScannerInput(start);

            Log.Information("Add scan request {ScanId} to in-memory list", scanId);
            var scan = new Scan(scanId, siteCollectionQueue, options)
            {
                SiteCollectionsToScan = siteCollectionList.Count,
                Status = ScanStatus.Queued,
            };

            // Ensure scan is added to the in-memory list as once a site collection is enqueud it
            // starts executing and will check the in-memory list
            if (!scans.TryAdd(scanId, scan))
            {
                Log.Error("Scan request was not added to the list of running scans");
                throw new Exception("Scan request was not added to the list of running scans");
            }

            Log.Information("Start enqueuing {SiteCollectionCount} site collections for scan {ScanId}", siteCollectionList.Count, scanId);
            // Enqueue the received site collections
            foreach (var site in siteCollectionList)
            {
                await siteCollectionQueue.EnqueueAsync(new SiteCollectionQueueItem(options, site));
            }
            Log.Information("Enqueued {SiteCollectionCount} site collections for scan {ScanId}", siteCollectionList.Count, scanId);

            // We're done
            Log.Information("Scan started for scan {ScanId}!", scanId);

            return scanId;
        }

        private void EnforeMaximumParallelRunningScans()
        {
            if (NumberOfScansRunning() >= MaxParallelScans)
            {
                Log.Error("Max number of parallel scans reached");
                throw new Exception("Max number of parallel scans reached");
            }
        }

        internal async Task RestartScanAsync(Guid scanId, Action<string> feedback)
        {
            Log.Information("Restarting scan {ScanId}", scanId);

            var listedScans = await GetScanListAsync(new ListRequest());
            var scanToRestart = listedScans.Status.FirstOrDefault(p => p.Id.Equals(scanId.ToString(), StringComparison.OrdinalIgnoreCase));
            if (scanToRestart == null)
            {
                feedback.Invoke($"Cannot restart scan {scanId} as it's unknown");
                Log.Warning("Cannot restart scan {ScanId} as it's unknown", scanId);
                return;
            }

            var scanStatus = (ScanStatus)Enum.Parse(typeof(ScanStatus), scanToRestart.Status);

            if (scanStatus == ScanStatus.Paused)
            {
                // Handle the scan restart
                await ProcessScanRestartAsync(scanId, (status) =>
                {
                    feedback.Invoke(status);
                });
            }
            else if (scanStatus == ScanStatus.Terminated)
            {
                // When a scan is terminated only the scan table status is set to Terminated, everything else 
                // just is what it was when the process terminated. First ensure the database is "consolidated"
                // before restarting the terminated scan
                feedback.Invoke($"Consolidating previously terminated scan {scanId} first");
                await StorageManager.ConsolidatedScanToEnableRestartAsync(scanId);

                // Handle the scan restart
                await ProcessScanRestartAsync(scanId, (status) => 
                { 
                    feedback.Invoke(status);
                });
            }
            else
            {
                feedback.Invoke($"Cannot restart scan {scanId} as it's status is {scanStatus}");
                Log.Warning("Cannot restart scan {ScanId} as it's status is {Status}", scanId, scanStatus);
                return;
            }
        }

        private async Task ProcessScanRestartAsync(Guid scanId, Action<string> feedback)
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

                // Launch a queue to handle this scan
                var siteCollectionQueue = new SiteCollectionQueue(this, StorageManager, scanId);

                // Configure the queue
                siteCollectionQueue.ConfigureQueue(ParallelSiteCollectionProcessingThreads);

                // Get the scan configuration options to use
                OptionsBase options = OptionsBase.FromScannerInput(start);

                var scan = new Scan(scanId, siteCollectionQueue, options)
                {
                    SiteCollectionsToScan = siteCollectionList.Count,
                    Status = ScanStatus.Queued,
                };

                // Important to add the scan to the in-memory list as after queueing a site collection
                // can start processing immediately and that requires the in-memory list entry
                if (!scans.TryAdd(scanId, scan))
                {
                    Log.Error("Scan restart request was not added to the list of running scans");
                    throw new Exception("Scan restart request was not added to the list of running scans");
                }

                Log.Information("Start enqueuing {SiteCollectionCount} site collections for restarting scan {ScanId}", siteCollectionList.Count, scanId);
                // Enqueue the received site collections
                foreach (var site in siteCollectionList)
                {
                    await siteCollectionQueue.EnqueueAsync(new SiteCollectionQueueItem(options, site) { Restart = true });
                }

                feedback.Invoke($"{siteCollectionList.Count} site collections queued for scanning again");

                // We're done
                Log.Information("Enqueued {SiteCollectionCount} site collections for restarting scan {ScanId}", siteCollectionList.Count, scanId);
            }
            else
            {
                Log.Information("No pending site collections to be scanned when restarting scan {ScanId}", scanId);
            }
        }

#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
        internal async Task<StatusReply> GetScanStatusAsync()
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
        {
            var statusReply = new StatusReply();

            foreach (var runningScan in scans)
            {
                statusReply.Status.Add(new ScanStatusReply
                {
                    Id = runningScan.Value.Id.ToString(),
                    Status = runningScan.Value.Status.ToString(),
                    SiteCollectionsToScan = runningScan.Value.SiteCollectionsToScan,
                    SiteCollectionsScanned = runningScan.Value.SiteCollectionsScanned,
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

        internal async Task SetPausingStatusAsync(Guid scanId, bool all, ScanStatus pauseMode)
        {
            List<Guid> scansToPause = new();
            if (all)
            {
                Log.Information("Setting {PauseMode} bit for all running scans", pauseMode);
                lock (scanListLock)
                {
                    foreach(var scan in scans)
                    {
                        scan.Value.Status = pauseMode;
                        scansToPause.Add(scan.Key);
                    }
                }
            }
            else
            {
                Log.Information("Setting {PauseMode} bit for scan {ScanId}", pauseMode, scanId);
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

            if (pauseMode == ScanStatus.Paused)
            {
                ClearFinishedOrPausedScansFromMemory();
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
                Log.Warning("Error while running IsPausing for scan {ScanId}. Error = scan if not listed yet", scanId);
                return false;
            }
        }

        internal bool ScanExists(Guid scanId)
        {
            // Ensure the in-memory table is updated to avoid adding duplicate entries
            ClearFinishedOrPausedScansFromMemory();

            return scans.ContainsKey(scanId);
        }

        internal async Task PrepareDatabaseForPauseAsync(Guid scanId, bool all)
        {
            List<Guid> scansToPause = new();
            if (all)
            {
                foreach (var scan in scans)
                {
                    scansToPause.Add(scan.Key);
                }
            }
            else
            {
                scansToPause.Add(scanId);
            }

            foreach(var scan in scansToPause)
            {
                await StorageManager.ConsolidatedScanToEnableRestartAsync(scan);
            }
        }

        internal async Task WaitForPendingWebScansAsync(Guid scanId, bool all, int maxChecks = 30, int delay = 10)
        {
            bool pendingWebScans = true;
            int checksDone = 0;

            List<Guid> scansToPause = new();
            if (all)
            {
                foreach (var scan in scans)
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
                return;
            }

            do
            {
                Log.Information("Check for running web scans for scan {ScanId}", scanId);

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
                    Log.Information("Running web scans for scan {ScanId}, waiting {Delay} seconds", scanId, delay);
                    checksDone++;
                    await Task.Delay(TimeSpan.FromSeconds(delay));
                }
            }
            while (pendingWebScans && checksDone <= maxChecks);
        }

        internal async Task UpdateScanStatusAsync(Guid scanId, ScanStatus scanStatus)
        {
            Log.Information("Updating scan status for scan {ScanId} to {ScanStatus}", scanId, scanStatus);

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
                Log.Information("Updating scan status for scan {ScanId} to {ScanStatus} in database", scanId, scanStatus);
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
            Log.Information("A site collection was fully scanned for scan {ScanId}", scanId);
        }

        internal int NumberOfScansRunning()
        {
            int running = 0;
            foreach (var scan in scans)
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
            Log.Warning("Kestrel is stopping");

            // Mark what's running as terminated since we're killing the server process
            MarkRunningScansAsTerminatedAsync().GetAwaiter().GetResult();
            
            Log.Warning("Running scans marked as terminated, Kestrel can shutdown now");
        }

        private void OnStopped()
        {
            Log.Warning("Kestrel stopped");
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
                    await UpdateScanStatusAsync(scanId, ScanStatus.Finished);

                    await StorageManager.EndScanAsync(scanId);
                }

            }
        }

        private async Task MarkRunningScansAsTerminatedAsync()
        {
            var listedScans = await ScanEnumerationManager.EnumerateScansFromDiskAsync(StorageManager, true, false, false, false);

            int count = 0;
            foreach(var scan in listedScans.Status)
            {
                Log.Information("Marking scan {ScanId} as Terminated", scan.Id);
                await StorageManager.SetScanStatusAsync(Guid.Parse(scan.Id), ScanStatus.Terminated);
                count++;
            }

            Log.Information("{Count} scans are marked as terminated", count);
        }

        private async Task ClearFinishedOrPausedScansFromMemoryAsync()
        {
            bool busy = true;
            while (busy)
            {
                // Check runs once per minute
                await Task.Delay(TimeSpan.FromMinutes(1));

                ClearFinishedOrPausedScansFromMemory();
            }
        }

        private void ClearFinishedOrPausedScansFromMemory()
        {
            foreach (var scan in scans.ToList())
            {
                if (scan.Value.Status == ScanStatus.Finished || scan.Value.Status == ScanStatus.Paused)
                {
                    if (scans.TryRemove(scan))
                    {
                        Log.Information("Removing finished scan {ScanId} from the memory list", scan.Key);
                    }
                    else
                    {
                        Log.Warning("Failed removing finished scan {ScanId} from the memory list", scan.Key);
                    }
                }
            }
        }
    }
}
