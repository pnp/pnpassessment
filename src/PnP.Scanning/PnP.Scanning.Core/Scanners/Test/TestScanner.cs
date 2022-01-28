using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.Scanners
{
    internal sealed class TestScanner: ScannerBase
    {
        private const int minDelay = 500;
        private const int maxDelay = 10000;
        private const string Cache1 = "Cache1";

        internal TestScanner(ScanManager scanManager, StorageManager storageManager, Guid scanId, string siteUrl, string webUrl, TestOptions options) : base(scanManager, storageManager, scanId, siteUrl, webUrl)
        {
            Options = options;            
        }

        internal TestOptions Options { get; set; }

        internal async override Task ExecuteAsync()
        {
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
            await StorageManager.SaveTestScanResultsAsync(ScanId, SiteUrl, WebUrl, delay1, delay2, delay3);
        }

        internal async override Task PreScanningAsync()
        {
            Logger.Information("Pre scanning work is starting");
            
            int delay1 = new Random().Next(minDelay, minDelay * 5);
            await Task.Delay(delay1);

            // Logic that randomly throws an error for 10% of the prescans
            int throwException = new Random().Next(1, 10);
            if (throwException == 10)
            {
                throw new Exception($"Something went wrong during prescanning with the test scanner with options {Options.TestNumberOfSites}!!");
            }

            AddToCache(Cache1, $"PnP Rocks! - {DateTime.Now}");

            Logger.Information("Pre scanning work done");
        }
    }
}
