using PnP.Core.Services;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.Scanners
{
    internal class SyntexScanner : ScannerBase
    {
        public SyntexScanner(ScanManager scanManager, StorageManager storageManager, IPnPContextFactory pnpContextFactory, Guid scanId, string siteUrl, string webUrl, SyntexOptions options) : base(scanManager, storageManager, pnpContextFactory, scanId, siteUrl, webUrl)
        {
            Options = options;
        }

        internal SyntexOptions Options { get; set; }


        internal async override Task ExecuteAsync()
        {
            using (var context = await GetPnPContextAsync())
            {

                
                //await StorageManager.SaveTestScanResultsAsync(ScanId, SiteUrl, WebUrl, delay1, delay2, delay3, context.Web.Id.ToString());
            }
        }

        internal async override Task PreScanningAsync()
        {
            //Logger.Information("Pre scanning work is starting");

            //AddToCache(Cache1, $"PnP Rocks! - {DateTime.Now}");

            //Logger.Information("Pre scanning work done");
        }

    }
}
