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
        private ActionBlock<WebQueueItem> websToScan;

        public WebQueue(ScanManager scanManager, StorageManager storageManager, Guid scanId, CancellationToken cancellationToken) : base(storageManager, cancellationToken)
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
                    CancellationToken = CancellationToken
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
            if (!ScanManager.IsPausing(ScanId) && !CancellationToken.IsCancellationRequested)
            {
                // Add a random wait to avoid contention when a large parallel scan kicks in
                await Task.Delay(TimeSpan.FromMilliseconds(new Random().Next(0, 250)));

                await StorageManager.StartWebScanAsync(ScanId, web.SiteCollectionUrl, web.WebUrl);

                // Get an instance for the actual scanner to use
                var scanner = ScannerBase.NewScanner(ScanManager, StorageManager, web.PnPContextFactory, web.CsomEventHub, ScanId, web.SiteCollectionUrl, web.WebUrl, web.OptionsBase);

                if (scanner == null)
                {
                    Log.Error("Unknown options class specified for assessment {ScanId}, no assessment instance created", ScanId);
                    throw new Exception($"Unknown options class specified for assessment {ScanId}, no assessment instance created");
                }

                try
                {
                    // Execute the actual scan logic for the loaded web
                    await scanner.ExecuteAsync();

                    // Give some room for the other processing threads to handle pause operations
                    await Task.Delay(TimeSpan.FromMilliseconds(250));

                    // Mark the web was scanned
                    await StorageManager.EndWebScanAsync(ScanId, web.SiteCollectionUrl, web.WebUrl);
                }
                catch (Exception ex)
                {
                    // The web scan failed, log accordingly
                    Log.Error(ex, "Assessment of {SiteUrl}{WebUrl} failed with assessment component {ScanComponent} error '{Error}'", web.SiteCollectionUrl, web.WebUrl, scanner.GetType(), ex.Message);
                    await StorageManager.EndWebScanWithErrorAsync(ScanId, web.SiteCollectionUrl, web.WebUrl, ex);
                }
            }
            else
            {
                if (CancellationToken.IsCancellationRequested)
                {
                    Log.Information("Assessment {ScanId} was cancelled, so skipping processing of web {SiteCollection}{WebUrl}.", ScanId, web.SiteCollectionUrl, web.WebUrl);
                }
                else
                {
                    Log.Information("Assessment {ScanId} has pausing bit set, so skipping processing of web {SiteCollection}{WebUrl}.", ScanId, web.SiteCollectionUrl, web.WebUrl);
                }
            }
        }
        
    }
}
