using Microsoft.Extensions.Logging;
using System.Threading.Tasks.Dataflow;

namespace PnP.Scanning.Core.Queues
{
    internal sealed class SiteCollectionQueue : QueueBase<SiteCollectionQueue>
    {
        // Queue containting the tasks to process
        private ActionBlock<string>? siteCollectionsToScan;
        private readonly ILoggerFactory loggerFactory;

        public SiteCollectionQueue(ILoggerFactory loggerFactory) : base(loggerFactory)
        {
            this.loggerFactory = loggerFactory;
        }

        internal async Task EnqueueAsync(string siteCollectionUrl)
        {
            if (siteCollectionsToScan == null)
            {
                var executionDataflowBlockOptions = new ExecutionDataflowBlockOptions()
                {
                    MaxDegreeOfParallelism = ParallelThreads,
                };

                // Configure the site collection scanning queue
                siteCollectionsToScan = new ActionBlock<string>(async (siteCollectionUrl) => await ProcessSiteCollectionAsync(siteCollectionUrl)
                                                                , executionDataflowBlockOptions);
            }
            
            // Send the request into the queue
            await siteCollectionsToScan.SendAsync(siteCollectionUrl);
        }

        private async Task ProcessSiteCollectionAsync(string siteCollectionUrl)
        {
            // Get the sub sites in the given site collection
            List<string> webToScan = new();

            for (int i = 0; i < new Random().Next(10); i++)
            {
                webToScan.Add($"{siteCollectionUrl}/subsite{i}");
            }

            // Start parallel execution per web in this site collection
            var webQueue = new WebQueue(this.loggerFactory);
            webQueue.ConfigureQueue(1);
            foreach (var web in webToScan)
            {
                await webQueue.EnqueueAsync(web);
            }            
        }

    }
}
