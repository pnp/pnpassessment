using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using Serilog;
using System.Threading.Tasks.Dataflow;

namespace PnP.Scanning.Core.Queues
{
    internal sealed class SiteCollectionQueue : QueueBase<SiteCollectionQueue>
    {
        // Queue containting the tasks to process
        private ActionBlock<SiteCollectionQueueItem>? siteCollectionsToScan;

        public SiteCollectionQueue(ScanManager scanManager, StorageManager storageManager, Guid scanId) : base(storageManager)
        {
            ScanManager = scanManager;
            ScanId = scanId;
        }

        internal int ParallelWebProcessingThreads { get; private set; } = 2;

        private ScanManager ScanManager { get; set; }

        private Guid ScanId { get; set; }

        internal void ConfigureParallelProcessing(int threads)
        {
            if (threads < 1)
            {
                Log.Information("Threads for scan {ScanId} was {Threads}. Setting it to it's default again {Default}", ScanId, threads, Environment.ProcessorCount);
                threads = Environment.ProcessorCount;
            }

            if (threads == 1)
            {
                Log.Information("Using {SiteThread} site collection scan thread and {WebThread} web scan thread", 1, 1);
                // Typically used for debugging work
                ParallelWebProcessingThreads = 1;
            }
            else
            {
                int siteThreads = threads / ParallelWebProcessingThreads;
                Log.Information("Using {SiteThread} site collection scan threads and {WebThread} web scan threads", siteThreads, ParallelWebProcessingThreads);
                ConfigureQueue(siteThreads);
            }
        }

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

                Log.Information("Site collection queue for scan {ScanId} setup", ScanId);
            }
            
            // Send the request into the queue
            await siteCollectionsToScan.SendAsync(siteCollection);
        }

        private async Task ProcessSiteCollectionAsync(SiteCollectionQueueItem siteCollection)
        {
            // Check the pausing bit, if so then we'll skip processing this site collection
            if (!ScanManager.IsPausing(ScanId))
            {
                // Mark the scan status as running (if not yet done)
                await ScanManager.UpdateScanStatusAsync(ScanId, ScanStatus.Running);

                // Mark the site collection as starting with scanning
                await StorageManager.StartSiteCollectionScanAsync(ScanId, siteCollection.SiteCollectionUrl);

                // Get the sub sites in the given site collection
                
                // Enumerate the webs to scan
                var webUrlsToScan = await ScanManager.SiteEnumerationManager.EnumerateWebsToScanAsync(ScanId, siteCollection.SiteCollectionUrl, siteCollection.OptionsBase, siteCollection.Restart);
                
                // Build list of web queue items to be processed
                List<WebQueueItem> webToScan = new();
                foreach (var web in webUrlsToScan)
                {
                    webToScan.Add(new WebQueueItem(siteCollection.OptionsBase,
                                                   siteCollection.SiteCollectionUrl,
                                                   web.WebUrl));
                }
            
                // Store the webs to be processed, for a restart the webs might already be there
                await StorageManager.StoreWebsToScanAsync(ScanId, siteCollection.SiteCollectionUrl, webUrlsToScan, siteCollection.Restart);

                // Start parallel execution per web in this site collection
                var webQueue = new WebQueue(ScanManager, StorageManager, ScanId);
                
                // Use parallel threads per running site collection task for processing the webs
                webQueue.ConfigureQueue(ParallelWebProcessingThreads);

                foreach (var web in webToScan)
                {
                    await webQueue.EnqueueAsync(web);
                }

                // Wait until the queue is completely drained
                webQueue.WaitForCompletion();

                // Increase the site collections scanned in memory counter
                ScanManager.SiteCollectionScanned(ScanId);

                // Some of the webs of the site collection could have been paused, if so
                // the web must not be be set to done
                if (!ScanManager.IsPausing(ScanId))
                {
                    await StorageManager.EndSiteCollectionScanAsync(ScanId, siteCollection.SiteCollectionUrl);
                }
                else
                {
                    // When pausing is ongoing doing "nothing" will leave the site collection in queued status, however if the pausing
                    // kicked in while the last webs of a given site collection were being already processed then in the end all the 
                    // webs are done and the site collection should be marked as "Finished"
                    if (await StorageManager.SiteCollectionWasCompletelyHandledAsync(ScanId, siteCollection.SiteCollectionUrl))
                    {
                        Log.Information("The scan {ScanId} is being paused, but as all webs of site collection {SiteCollectionUrl} were done mark it as Finished", ScanId, siteCollection.SiteCollectionUrl);
                        await StorageManager.EndSiteCollectionScanAsync(ScanId, siteCollection.SiteCollectionUrl);
                    }
                }
            }
            else
            {
                Log.Information("Scan {ScanId} has pausing bit set, so skipping processing of site collection {SiteCollection}.", ScanId, siteCollection.SiteCollectionUrl);
            }
        }        

    }
}
