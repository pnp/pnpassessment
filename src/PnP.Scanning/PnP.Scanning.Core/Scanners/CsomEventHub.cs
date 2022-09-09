using PnP.Core.Services;

namespace PnP.Scanning.Core.Scanners
{
    internal sealed class CsomEventHub
    {
        /// <summary>
        /// Delegate for the <see cref="RequestRateLimitWaitAsync"/> event
        /// </summary>
        /// <param name="cancellationToken">Current cancellation token</param>
        /// <returns></returns>
        public delegate Task RequestRateLimitWaitDelegate(CancellationToken cancellationToken);
        
        /// <summary>
        /// Default constructor
        /// </summary>
        public CsomEventHub()
        {
        }

        /// <summary>
        /// Event to subscribe to get notified whenever a CSOM request is getting retried due to throttling or an error
        /// </summary>
        public Action<CsomRetryEvent> RequestRetry { get; set; }

        /// <summary>
        /// Event so subscribe to for getting event rate limit information from a CSOM request
        /// </summary>
        public Action<RateLimitEvent> RequestRateLimitUpdate { get; set; }

        /// <summary>
        /// Event to subscribe to for implementing a delay for a a CSOM request due to the rate limit information received via <see cref="RequestRateLimitUpdate"/>.
        /// </summary>        
        public RequestRateLimitWaitDelegate RequestRateLimitWaitAsync { get; set; }
    }
}
