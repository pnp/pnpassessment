using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using Serilog;
using System.Threading.Tasks.Dataflow;

namespace PnP.Scanning.Core.Queues
{
    internal sealed class WebQueue : QueueBase<WebQueue>
    {
        // Queue containting the tasks to process
        private ActionBlock<WebQueueItem>? websToScan;

        public WebQueue(ScanManager scanManager, StorageManager storageManager, Guid scanId): base(storageManager)
        {
            ScanId = scanId;
            ScanManager = scanManager;
        }

        private ScanManager ScanManager { get; set; }

        private Guid ScanId { get; set; }

        internal async Task EnqueueAsync(WebQueueItem web)
        {
            if (websToScan == null)
            {
                var executionDataflowBlockOptions = new ExecutionDataflowBlockOptions()
                {
                    SingleProducerConstrained = true,
                    MaxDegreeOfParallelism = ParallelThreads,
                };

                // Configure the site collection scanning queue
                websToScan = new ActionBlock<WebQueueItem>(async (web) => await ProcessWebAsync(web)
                                                                , executionDataflowBlockOptions);
            }

            // Send the request into the queue
            await websToScan.SendAsync(web);
        }

        internal void WaitForCompletion()
        {
            if (websToScan != null)
            {
                websToScan.Complete();
                websToScan.Completion.Wait();
            }
        }

        private async Task ProcessWebAsync(WebQueueItem web)
        {
            if (!ScanManager.IsPausing(ScanId))
            {
                await StorageManager.StartWebScanAsync(ScanId, web.SiteCollectionUrl, web.WebUrl);

                // Get an instance for the actual scanner to use
                var scanner = ScannerBase.NewScanner(ScanManager, StorageManager, web.PnPContextFactory, ScanId, web.SiteCollectionUrl, web.WebUrl, web.OptionsBase);

                if (scanner == null)
                {
                    Log.Error("Unknown options class specified for scan {ScanId}, no scanner instance created", ScanId);
                    throw new Exception($"Unknown options class specified for scan {ScanId}, no scanner instance created");
                }

                try
                {
                    // Execute the actual scan logic for the loaded web
                    await scanner.ExecuteAsync();

                    // Mark the web was scanned
                    await StorageManager.EndWebScanAsync(ScanId, web.SiteCollectionUrl, web.WebUrl);
                }
                catch (Exception ex)
                {
                    // The web scan failed, log accordingly
                    Log.Error(ex, "Scan of {SiteUrl}{WebUrl} failed with  scan component {ScanComponent} error '{Error}'", web.SiteCollectionUrl, web.WebUrl, scanner.GetType(), ex.Message);
                    await StorageManager.EndWebScanWithErrorAsync(ScanId, web.SiteCollectionUrl, web.WebUrl, ex);
                }
            }
            else
            {
                Log.Information("Scan {ScanId} has pausing bit set, so skipping processing of web {SiteCollection}{WebUrl}.", ScanId, web.SiteCollectionUrl, web.WebUrl);
            }
        }
        
    }
}
