using PnP.Scanning.Core.Storage;
using Serilog;

namespace PnP.Scanning.Core.Scanners
{
    internal abstract class ScannerBase
    {
        internal ScannerBase(StorageManager storageManager, Guid scanId, string siteUrl, string webUrl)
        {
            StorageManager = storageManager;
            ScanId = scanId;
            SiteUrl = siteUrl;
            WebUrl = webUrl;
            Logger = Log.ForContext("ScanId", scanId);
        }

        internal string WebUrl { get; set; }

        internal string SiteUrl { get; set; }

        internal StorageManager StorageManager { get; private set; }

        internal Guid ScanId { get; set; }

        internal ILogger Logger { get; private set; }

#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
        internal virtual async Task PreScanningAsync()
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
        {
        }

#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
        internal virtual async Task ExecuteAsync()
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
        {
        }

        internal static ScannerBase? NewScanner(StorageManager storageManager, Guid scanId, string siteCollectionUrl, string webUrl, OptionsBase options)
        {

#if DEBUG
            if (options is TestOptions testOptions)
            {
                return new TestScanner(storageManager, scanId, siteCollectionUrl, webUrl, testOptions);
            }
#endif

            return null;
        }
    }
}
