using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using Serilog;
using System.Threading.Tasks.Dataflow;
using PnP.Scanning.Core.Discovery;

namespace PnP.Scanning.Core.Queues
{
    internal sealed class WebQueue : QueueBase<WebQueue>
    {
        // Queue containting the tasks to process
        private ActionBlock<WebQueueItem> websToScan;

        public WebQueue(ScanManager scanManager, StorageManager storageManager, Guid scanId, CancellationToken cancellationToken, string adminCenterUrl, string mySiteHostUrl) : base(storageManager, cancellationToken, adminCenterUrl, mySiteHostUrl)
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
                    BoundedCapacity = Math.Max(1, ParallelThreads * 2),
                    CancellationToken = CancellationToken
                };

                // Configure the site collection scanning queue
                websToScan = new ActionBlock<WebQueueItem>(async (web) => await ProcessWebAsync(web)
                                                                , executionDataflowBlockOptions);
            }

            // Send the request into the queue
            await websToScan.SendAsync(web);
        }

        internal async Task WaitForCompletionAsync()
        {
            if (websToScan != null)
            {
                websToScan.Complete();
                await websToScan.Completion.ConfigureAwait(false);
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
                var scanner = ScannerBase.NewScanner(ScanManager, StorageManager, web.PnPContextFactory, ScanId, web.SiteCollectionUrl, web.WebUrl, web.WebTemplate, web.OptionsBase, AdminCenterUrl, MySiteHostUrl);

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
                    if (web.OptionsBase is ClassicOptions { Pages: true })
                    {
                        var coverage = await new AssessmentDiscoveryWriter(ScanId)
                            .ReadWebCoverageAsync(ScanId, web.SiteCollectionUrl, web.WebUrl);
                        await ClassicPageDiscoveryComponent.RecordScopeAsync(ScanId, web.SiteCollectionUrl, web.WebUrl,
                            "Web", coverage, stage: "WebScan");
                    }
                }
                catch (Exception ex)
                {
                    if (web.OptionsBase is ClassicOptions { Pages: true })
                        await ClassicPageDiscoveryComponent.RecordScopeAsync(ScanId, web.SiteCollectionUrl, web.WebUrl,
                            "Web", CancellationToken.IsCancellationRequested ? "Cancelled" :
                                AssessmentWebDiscovery.Status(AssessmentWebDiscovery.Classify(ex)), ex, stage: "WebScan");
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
