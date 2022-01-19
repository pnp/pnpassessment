using Microsoft.Extensions.Logging;
using PnP.Scanning.Core.Queues;
using PnP.Scanning.Core.Scanners;
using System.Collections.Concurrent;

namespace PnP.Scanning.Core.Services
{
    // Class for in memory tracking the running scans
    internal sealed class ScanManager
    {
        private object updateLock = new object();
        private readonly ILoggerFactory loggerFactory;
        private readonly ILogger logger;
        private readonly ConcurrentDictionary<Guid, Scan> scans = new();

        public ScanManager(ILoggerFactory loggerFactory)
        {
            this.loggerFactory = loggerFactory;
            logger = loggerFactory.CreateLogger<ScanManager>();

            // Launch a thread that will monitor and update the list of scans
            Task.Run(() => AutoUpdateRunningScans());
        }

        internal int MaxParallelScans { get; private set; } = 3;

        internal async Task<Guid> StartScanAsync(StartRequest start, List<string> siteCollectionList)
        {
            if (NumberOfScansRunning() >= MaxParallelScans)
            {
                throw new Exception("Max number of parallel scans reached");
            }

            Guid scanId = Guid.NewGuid();

            // Launch a queue to handle this scan
            var siteCollectionQueue = new SiteCollectionQueue(loggerFactory, this, scanId);

            // Configure the queue
            siteCollectionQueue.ConfigureQueue(4);

            // Get the scan configuration options to use
            OptionsBase options = OptionsBase.FromScannerInput(start);

            // Enqueue the received site collections
            foreach (var site in siteCollectionList)
            {
                await siteCollectionQueue.EnqueueAsync(new SiteCollectionQueueItem(options, site));
            }

            var scan = new Scan(scanId, siteCollectionQueue, options)
            {
                SiteCollectionsToScan = siteCollectionList.Count,
                Status = ScanStatus.Queued,
            };

            if (!scans.TryAdd(scanId, scan))
            {
                throw new Exception("Scan request was not added to the list of running scans");
            }

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
            lock (updateLock)
            {
                scans[scanId].Status = scanStatus;
            }
        }

        internal void SiteCollectionScanned(Guid scanId)
        {
            scans[scanId].SiteCollectionWasScanned();
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

        private void AutoUpdateRunningScans()
        {
            bool busy = true;
            while (busy)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(500));

                foreach (var scan in scans.ToList())
                {
                    if ((scan.Value.Status == ScanStatus.Queued || scan.Value.Status == ScanStatus.Running) &&
                         scan.Value.SiteCollectionsScanned == scan.Value.SiteCollectionsToScan)
                    {
                        UpdateScanStatus(scan.Value.Id, ScanStatus.Finished);
                    }
                }

            }
        }

    }
}
