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

        public ScanManager(IHostApplicationLifetime hostApplicationLifetime, StorageManager storageManager)
        {
            this.hostApplicationLifetime = hostApplicationLifetime;
            // Hook the application stopping as that allows for cleanup 
            this.hostApplicationLifetime.ApplicationStopping.Register(OnStopping);
            this.hostApplicationLifetime.ApplicationStopped.Register(OnStopped);

            StorageManager = storageManager;

            // Launch a thread that will mark the running scans as terminated
            Task.Run(async () => await MarkRunningScansAsTerminatedAsync());

            // Launch a thread that will monitor and update the list of scans
            Task.Run(async () => await AutoUpdateRunningScansAsync());
        }

        internal StorageManager StorageManager { get; private set; }

        internal int MaxParallelScans { get; private set; } = 3;

        internal int ParallelThreads { get; private set; } = 4;

        internal async Task<Guid> StartScanAsync(StartRequest start, List<string> siteCollectionList)
        {
            Log.Information("Starting the scan job");

            if (NumberOfScansRunning() >= MaxParallelScans)
            {
                Log.Error("Max number of parallel scans reached");
                throw new Exception("Max number of parallel scans reached");
            }

            Guid scanId = Guid.NewGuid();

            Log.Information("Scan id is {ScanId}", scanId);

            await StorageManager.LaunchNewScanAsync(scanId, start, siteCollectionList);

            // Launch a queue to handle this scan
            var siteCollectionQueue = new SiteCollectionQueue(this, StorageManager, scanId);

            // Configure the queue
            siteCollectionQueue.ConfigureQueue(ParallelThreads);

            // Get the scan configuration options to use
            OptionsBase options = OptionsBase.FromScannerInput(start);

            Log.Information("Start enqueuing {SiteCollectionCount} site collections for scan {ScanId}", siteCollectionList.Count, scanId);
            // Enqueue the received site collections
            foreach (var site in siteCollectionList)
            {
                await siteCollectionQueue.EnqueueAsync(new SiteCollectionQueueItem(options, site));
            }

            Log.Information("Enqueued {SiteCollectionCount} site collections for scan {ScanId}", siteCollectionList.Count, scanId);

            var scan = new Scan(scanId, siteCollectionQueue, options)
            {
                SiteCollectionsToScan = siteCollectionList.Count,
                Status = ScanStatus.Queued,
            };

            if (!scans.TryAdd(scanId, scan))
            {
                Log.Error("Scan request was not added to the list of running scans");
                throw new Exception("Scan request was not added to the list of running scans");
            }

            Log.Information("Scan started for scan {ScanId}!", scanId);

            return scanId;
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

        internal async Task SetPausingBitAsync(Guid scanId, bool all, ScanStatus pauseMode)
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
        }

        internal bool IsPausing(Guid scanId)
        {
            // Import to also protect this operation via the scan list lock as otherwise it can result in deadlocks
            lock (scanListLock)
            {
                return scans[scanId].Status == ScanStatus.Pausing || scans[scanId].Status == ScanStatus.Paused;
            }
        }

        internal bool ScanExists(Guid scanId)
        {
            lock (scanListLock)
            {
                return scans.ContainsKey(scanId);
            }
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
                await StorageManager.PauseScanAsync(scan);
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
            }
        }

        internal void SiteCollectionScanned(Guid scanId)
        {
            scans[scanId].SiteCollectionWasScanned();
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
                await Task.Delay(TimeSpan.FromMilliseconds(500));

                foreach (var scan in scans.ToList())
                {
                    if ((scan.Value.Status == ScanStatus.Queued || scan.Value.Status == ScanStatus.Running) &&
                         scan.Value.SiteCollectionsScanned == scan.Value.SiteCollectionsToScan)
                    {
                        await UpdateScanStatusAsync(scan.Value.Id, ScanStatus.Finished);

                        await StorageManager.EndScanAsync(scan.Value.Id);
                    }
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

    }
}
