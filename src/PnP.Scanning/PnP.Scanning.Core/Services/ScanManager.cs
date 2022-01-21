using PnP.Scanning.Core.Queues;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Storage;
using Serilog;
using System.Collections.Concurrent;

namespace PnP.Scanning.Core.Services
{
    // Class for in memory tracking the running scans
    internal sealed class ScanManager
    {
        private object updateLock = new object();
        private readonly ConcurrentDictionary<Guid, Scan> scans = new();

        public ScanManager(StorageManager storageManager)
        {
            StorageManager = storageManager;

            // Launch a thread that will monitor and update the list of scans
            Task.Run(async () => await AutoUpdateRunningScansAsync());
        }

        internal StorageManager StorageManager { get; private set; }

        internal int MaxParallelScans { get; private set; } = 3;

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
            siteCollectionQueue.ConfigureQueue(4);

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

        internal void UpdateScanStatus(Guid scanId, ScanStatus scanStatus)
        {
            Log.Information("Updating scan status for scan {ScanId} to {ScanStatus}", scanId, scanStatus);

            lock (updateLock)
            {
                scans[scanId].Status = scanStatus;
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
                        UpdateScanStatus(scan.Value.Id, ScanStatus.Finished);

                        await StorageManager.EndScanAsync(scan.Value.Id);
                    }
                }

            }
        }

    }
}
