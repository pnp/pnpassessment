using Microsoft.Extensions.Logging;
using PnP.Scanning.Core.Services;
using System.Threading.Tasks.Dataflow;

namespace PnP.Scanning.Core.Queues
{
    internal sealed class SiteCollectionQueue : QueueBase<SiteCollectionQueue>
    {
        // Queue containting the tasks to process
        private ActionBlock<SiteCollectionQueueItem>? siteCollectionsToScan;
        private readonly ILoggerFactory loggerFactory;        

        public SiteCollectionQueue(ILoggerFactory loggerFactory, ScanManager scanManager, Guid scanId) : base(loggerFactory)
        {
            this.loggerFactory = loggerFactory;
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

                // Configure the site collection scanning queue
                siteCollectionsToScan = new ActionBlock<SiteCollectionQueueItem>(async (siteCollection) => await ProcessSiteCollectionAsync(siteCollection)
                                                                , executionDataflowBlockOptions);
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
            Logger.LogWarning($"Number of webs to scan: {numberOfWebs}");

            for (int i = 0; i < numberOfWebs; i++)
            {
                webToScan.Add(new WebQueueItem(siteCollection.OptionsBase, 
                                               siteCollection.SiteCollectionUrl, 
                                               $"{siteCollection.SiteCollectionUrl}/subsite{i}"));
            }

            // Start parallel execution per web in this site collection
            var webQueue = new WebQueue(loggerFactory);
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
