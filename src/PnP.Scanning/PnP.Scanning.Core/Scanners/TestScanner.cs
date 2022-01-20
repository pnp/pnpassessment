using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.Scanners
{
    internal sealed class TestScanner: ScannerBase
    {
        private const int minDelay = 500;
        private const int maxDelay = 10000;
        
        internal TestScanner(StorageManager storageManager, Guid scanId, string webUrl, TestOptions options) : base(storageManager, scanId)
        {
            WebUrl = webUrl;
            Options = options;            
        }

        internal string WebUrl { get; set; }

        internal TestOptions Options { get; set; }

        internal async override Task ExecuteAsync()
        {            
            Logger.Information("Started for {WebUrl} in scan {ScanId}. ThreadId : {ThreadId}", WebUrl, ScanId, Environment.CurrentManagedThreadId);
            int delay = new Random().Next(minDelay, maxDelay);
            await Task.Delay(delay);

            Logger.Information("Step 1 Delay {WebUrl} in scan {ScanId}. ThreadId : {ThreadId}", WebUrl, ScanId, Environment.CurrentManagedThreadId);
            delay = new Random().Next(minDelay, maxDelay);
            await Task.Delay(delay);

            Logger.Information("Step 2 Delay {WebUrl} in scan {ScanId}. ThreadId : {ThreadId}", WebUrl, ScanId, Environment.CurrentManagedThreadId);
            delay = new Random().Next(minDelay, maxDelay);
            await Task.Delay(delay);

            Logger.Information("Step 3 Delay {WebUrl} in scan {ScanId}. ThreadId : {ThreadId}", WebUrl, ScanId, Environment.CurrentManagedThreadId);
        }
    }
}
