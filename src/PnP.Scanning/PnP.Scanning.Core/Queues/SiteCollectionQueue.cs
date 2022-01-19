using PnP.Scanning.Core.Services;
using Serilog;
using System.Threading.Tasks.Dataflow;

namespace PnP.Scanning.Core.Queues
{
    internal sealed class SiteCollectionQueue : QueueBase<SiteCollectionQueue>
    {
        // Queue containting the tasks to process
        private ActionBlock<SiteCollectionQueueItem>? siteCollectionsToScan;

        public SiteCollectionQueue(ScanManager scanManager, Guid scanId) : base()
        {
            ScanManager = scanManager;
            ScanId = scanId;
        }

        private ScanManager ScanManager { get; set; }

        private Guid ScanId { get; set; }

        internal async Task EnqueueAsync(SiteCollectionQueueItem siteCollection)
        {
            if (siteCollectionsToScan == null)
            {
                var executionDataflowBlockOptions = new ExecutionDataflowBlockOptions()
                {
                    SingleProducerConstrained = true,
                    MaxDegreeOfParallelism = ParallelThreads,
                };

                Log.Information("Configuring site collection queue for scan {ScanId} with {MaxDegreeOfParallelism} max degree of parallelism", ScanId, executionDataflowBlockOptions.MaxDegreeOfParallelism);

                // Configure the site collection scanning queue
                siteCollectionsToScan = new ActionBlock<SiteCollectionQueueItem>(async (siteCollection) => await ProcessSiteCollectionAsync(siteCollection)
                                                                , executionDataflowBlockOptions);

                Log.Information("site collection queue for scan {ScanId} setup", ScanId);
            }
            
            // Send the request into the queue
            await siteCollectionsToScan.SendAsync(siteCollection);
        }

        private async Task ProcessSiteCollectionAsync(SiteCollectionQueueItem siteCollection)
        {
            // Mark the scan status as running
            ScanManager.UpdateScanStatus(ScanId, ScanStatus.Running);

            // Get the sub sites in the given site collection
            List<WebQueueItem> webToScan = new();

            int numberOfWebs = new Random().Next(10);
            Log.Information("Number of webs to scan: {WebsToScan}", numberOfWebs);

            for (int i = 0; i < numberOfWebs; i++)
            {
                webToScan.Add(new WebQueueItem(siteCollection.OptionsBase, 
                                               siteCollection.SiteCollectionUrl, 
                                               $"{siteCollection.SiteCollectionUrl}/subsite{i}"));
            }

            // Start parallel execution per web in this site collection
            var webQueue = new WebQueue(ScanId);
            // Use two parallel threads per running site collection task for processing the webs
            webQueue.ConfigureQueue(2);

            foreach (var web in webToScan)
            {
                await webQueue.EnqueueAsync(web);
            }

            // Wait until the queue is completely drained
            webQueue.WaitForCompletion();

            // Increase the site collections scanned in memory counter
            ScanManager.SiteCollectionScanned(ScanId);
        }        

    }
}
