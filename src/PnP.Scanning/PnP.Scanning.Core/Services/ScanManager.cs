using Microsoft.Extensions.Logging;

namespace PnP.Scanning.Core.Services
{
    internal sealed class ScanManager
    {
        private readonly ILogger logger;
        private int siteCollectionsScanned = 0;

        public ScanManager(ILoggerFactory loggerFactory)
        {
            logger = loggerFactory.CreateLogger<ScanManager>();
        }

        internal int SiteCollectionsToScan { get; set; }

        internal int SiteCollectionsScanned
        {
            get { return siteCollectionsScanned; }
        }

        internal void SiteCollectionScanned()
        {
            Interlocked.Increment(ref siteCollectionsScanned);
        }
    }
}
