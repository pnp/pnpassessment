#if DEBUG
using PnP.Core.Services;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.Scanners
{
    internal sealed class TestScanner: ScannerBase
    {
        private const int minDelay = 500;
        private const int maxDelay = 10000;
        private const string Cache1 = "Cache1";

        internal TestScanner(ScanManager scanManager, StorageManager storageManager, IPnPContextFactory pnpContextFactory, 
                             CsomEventHub csomEventHub, Guid scanId, string siteUrl, string webUrl, TestOptions options) : 
                             base(scanManager, storageManager, pnpContextFactory, csomEventHub, scanId, siteUrl, webUrl)
        {
            Options = options;            
        }

        internal TestOptions Options { get; set; }

        internal async override Task ExecuteAsync()
        {
            using (var context = await GetPnPContextAsync())
            {
                Logger.Warning("Web id is {WebId}", context.Web.Id);

                Logger.Information("Started for {SiteCollectionUrl}{WebUrl}. ThreadId : {ThreadId}", SiteUrl, WebUrl, Environment.CurrentManagedThreadId);
                int delay1 = new Random().Next(minDelay, maxDelay);
                await Task.Delay(delay1);

                Logger.Information("Cache contained key {Key} with value {Value}", Cache1, GetFromCache(Cache1));

                Logger.Information("Step 1 Delay {SiteCollectionUrl}{WebUrl}. ThreadId : {ThreadId}", SiteUrl, WebUrl, Environment.CurrentManagedThreadId);
                var delay2 = new Random().Next(minDelay, maxDelay);
                await Task.Delay(delay2);

                // Logic that randomly throws an error for 5% of the scanned webs
                int throwException = new Random().Next(1, 20);
                if (throwException == 10)
                {
                    throw new Exception($"Something went wrong in the test scanner with options {Options.TestNumberOfSites}!!");
                }

                Logger.Information("Step 2 Delay {SiteCollectionUrl}{WebUrl}. ThreadId : {ThreadId}", SiteUrl, WebUrl, Environment.CurrentManagedThreadId);
                var delay3 = new Random().Next(minDelay, maxDelay);
                await Task.Delay(delay3);

                Logger.Information("Step 3 Delay {SiteCollectionUrl}{WebUrl}. ThreadId : {ThreadId}", SiteUrl, WebUrl, Environment.CurrentManagedThreadId);

                // Save of the scanner outcome
                await SaveTestScanResultsAsync(ScanId, SiteUrl, WebUrl, delay1, delay2, delay3, context.Web.Id.ToString());
            }
        }

        internal async override Task PreScanningAsync()
        {
            Logger.Information("Pre assessment work is starting");
            
            int delay1 = new Random().Next(minDelay, minDelay * 5);
            await Task.Delay(delay1);

            // Logic that randomly throws an error for 10% of the prescans
            int throwException = new Random().Next(1, 10);
            if (throwException == 10)
            {
                throw new Exception($"Something went wrong during preassessment with the test assessment with options {Options.TestNumberOfSites}!!");
            }

            AddToCache(Cache1, $"PnP Rocks! - {DateTime.Now}");

            using (var context = await GetPnPContextAsync())
            {
                Logger.Warning("Pre assessment done for site with url {Url} and {Id}", context.Uri, context.Site.Id);
            }

            Logger.Information("Pre assessment work done");
        }

        internal async override Task PostScanningAsync()
        {
            Logger.Information("Post assessment work is starting");
            
            using (var context = await GetPnPContextAsync())
            {
                Logger.Warning("Post assessment done for site with url {Url} and {Id}", context.Uri, context.Site.Id);
            }

            Logger.Information("Post assessment work done");            
        }

        private async Task SaveTestScanResultsAsync(Guid scanId, string siteUrl, string webUrl, int delay1, int delay2, int delay3, string webIdString)
        {
            using (var dbContext = new ScanContext(ScanId))
            {
                dbContext.TestDelays.Add(new TestDelay
                {
                    ScanId = scanId,
                    SiteUrl = siteUrl,
                    WebUrl = webUrl,
                    Delay1 = delay1,
                    Delay2 = delay2,
                    Delay3 = delay3,
                    WebIdString = webIdString
                });

                await dbContext.SaveChangesAsync();
                Logger.Information("Database updates pushed in SaveTestScanResultsAsync");

            }
        }
    }
}
#endif