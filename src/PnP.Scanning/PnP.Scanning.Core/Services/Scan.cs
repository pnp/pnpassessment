using PnP.Scanning.Core.Authentication;
using PnP.Scanning.Core.Queues;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.Services
{
    internal sealed class Scan
    {
        private int siteCollectionsScanned = 0;
        private int requestWasThrottled = 0;
        private int requestWasRetriedDueToNetworkIssues = 0;

        internal Scan(Guid id, SiteCollectionQueue queue, OptionsBase options, AuthenticationManager authenticationManager, CancellationTokenSource cancellationTokenSource)
        {
            Id = id;
            Queue = queue;
            Options = options;
            AuthenticationManager = authenticationManager;
            StartedScanSessionAt = DateTime.Now;
            CancellationTokenSource = cancellationTokenSource;
        }

        internal Guid Id { get; private set; }

        internal CancellationTokenSource CancellationTokenSource { get; private set; }

        internal SiteCollectionQueue Queue { get; private set; }

        internal OptionsBase Options { get; private set; }

        internal AuthenticationManager AuthenticationManager { get; private set; }

        internal DateTime StartedScanSessionAt { get; private set; }

        internal ScanStatus Status { get; set; }

        internal int SiteCollectionsToScan { get; set; }
        
        internal string FirstSiteCollection { get; set; }

        internal int SiteCollectionsScanned 
        { 
            get 
            { 
                return siteCollectionsScanned; 
            }  
        }

        internal int RequestsThrottled
        {
            get
            {
                return requestWasThrottled;
            }
        }

        internal int RequestsRetriedDueToNetworkIssues
        {
            get
            {
                return requestWasRetriedDueToNetworkIssues;
            }
        }

        internal DateTime RetryingRequestAt { get; private set; }

        internal void SiteCollectionWasScanned()
        {
            Interlocked.Increment(ref siteCollectionsScanned);
        }

        internal void RequestWasThrottled(int waitTimeInSeconds)
        {
            Interlocked.Increment(ref requestWasThrottled);
            RetryingRequestAt = DateTime.Now.AddSeconds(waitTimeInSeconds);
        }

        internal void RequestsWasRetriedDueToNetworkIssues(int waitTimeInSeconds)
        {
            Interlocked.Increment(ref requestWasRetriedDueToNetworkIssues);
            RetryingRequestAt = DateTime.Now.AddSeconds(waitTimeInSeconds);
        }

        internal bool PostScanRunning { get; set; }
    }
}
