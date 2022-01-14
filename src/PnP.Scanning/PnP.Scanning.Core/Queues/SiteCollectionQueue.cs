using Microsoft.Extensions.Logging;
using System.Threading.Tasks.Dataflow;

namespace PnP.Scanning.Core.Queues
{
    internal sealed class SiteCollectionQueue : QueueBase<SiteCollectionQueue>
    {
        // Queue containting the tasks to process
        private ActionBlock<SiteCollectionQueueItem>? siteCollectionsToScan;
        private readonly ILoggerFactory loggerFactory;

        public SiteCollectionQueue(ILoggerFactory loggerFactory) : base(loggerFactory)
        {
            this.loggerFactory = loggerFactory;
        }

        internal async Task EnqueueAsync(SiteCollectionQueueItem siteCollection)
        {
            if (siteCollectionsToScan == null)
            {
                var executionDataflowBlockOptions = new ExecutionDataflowBlockOptions()
                {
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
            // Get the sub sites in the given site collection
            List<WebQueueItem> webToScan = new();

            for (int i = 0; i < new Random().Next(10); i++)
            {
                webToScan.Add(new WebQueueItem(siteCollection.OptionsBase, 
                                               siteCollection.SiteCollectionUrl, 
                                               $"{siteCollection.SiteCollectionUrl}/subsite{i}"));
            }

            // Start parallel execution per web in this site collection
            var webQueue = new WebQueue(loggerFactory);
            webQueue.ConfigureQueue(1);
            foreach (var web in webToScan)
            {
                await webQueue.EnqueueAsync(web);
            }            
        }

    }
}
