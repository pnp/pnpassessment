using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.Scanners
{
    internal sealed class TestScanner: ScannerBase
    {
        private const int minDelay = 500;
        private const int maxDelay = 10000;

        internal TestScanner(StorageManager storageManager, Guid scanId, string siteUrl, string webUrl, TestOptions options) : base(storageManager, scanId, siteUrl, webUrl)
        {
            Options = options;            
        }

        internal TestOptions Options { get; set; }

        internal async override Task ExecuteAsync()
        {
            Logger.Information("Started for {SiteCollectionUrl}{WebUrl} in scan {ScanId}. ThreadId : {ThreadId}", SiteUrl, WebUrl, ScanId, Environment.CurrentManagedThreadId);
            int delay1 = new Random().Next(minDelay, maxDelay);
            await Task.Delay(delay1);

            Logger.Information("Step 1 Delay {SiteCollectionUrl}{WebUrl} in scan {ScanId}. ThreadId : {ThreadId}", SiteUrl, WebUrl, ScanId, Environment.CurrentManagedThreadId);
            var delay2 = new Random().Next(minDelay, maxDelay);
            await Task.Delay(delay2);

            // Logic that randomly throws an error for 5% of the scanned webs
            int throwException = new Random().Next(1, 20);
            if (throwException == 10)
            {
                throw new Exception($"Something went wrong in the test scanner with options {Options.TestNumberOfSites}!!");
            }

            Logger.Information("Step 2 Delay {SiteCollectionUrl}{WebUrl} in scan {ScanId}. ThreadId : {ThreadId}", SiteUrl, WebUrl, ScanId, Environment.CurrentManagedThreadId);
            var delay3 = new Random().Next(minDelay, maxDelay);
            await Task.Delay(delay3);

            Logger.Information("Step 3 Delay {SiteCollectionUrl}{WebUrl} in scan {ScanId}. ThreadId : {ThreadId}", SiteUrl, WebUrl, ScanId, Environment.CurrentManagedThreadId);

            // Save of the scanner outcome
            await StorageManager.SaveTestScanResultsAsync(ScanId, SiteUrl, WebUrl, delay1, delay2, delay3);
        }
    }
}
