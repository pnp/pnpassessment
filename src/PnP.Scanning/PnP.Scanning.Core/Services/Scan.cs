using PnP.Scanning.Core.Queues;
using PnP.Scanning.Core.Scanners;

namespace PnP.Scanning.Core.Services
{
    internal sealed class Scan
    {
        private int siteCollectionsScanned = 0;

        internal Scan(Guid id, SiteCollectionQueue queue, OptionsBase options)
        {
            Id = id;
            Queue = queue;
            Options = options;
        }

        internal Guid Id { get; private set; }

        internal SiteCollectionQueue Queue { get; private set; }

        internal OptionsBase Options { get; private set; }

        internal ScanStatus Status { get; set; }

        internal int SiteCollectionsToScan { get; set; }

        internal int SiteCollectionsScanned 
        { 
            get 
            { 
                return siteCollectionsScanned; 
            }  
        }

        internal void SiteCollectionWasScanned()
        {
            Interlocked.Increment(ref siteCollectionsScanned);
        }
    }
}
