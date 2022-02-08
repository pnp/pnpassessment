using PnP.Scanning.Core.Authentication;
using PnP.Scanning.Core.Queues;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.Services
{
    internal sealed class Scan
    {
        private int siteCollectionsScanned = 0;

        internal Scan(Guid id, SiteCollectionQueue queue, OptionsBase options, AuthenticationManager authenticationManager)
        {
            Id = id;
            Queue = queue;
            Options = options;
            AuthenticationManager = authenticationManager;
            StartedScanSessionAt = DateTime.Now;
        }

        internal Guid Id { get; private set; }

        internal SiteCollectionQueue Queue { get; private set; }

        internal OptionsBase Options { get; private set; }

        internal AuthenticationManager AuthenticationManager { get; private set; }

        internal DateTime StartedScanSessionAt { get; private set; }

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
