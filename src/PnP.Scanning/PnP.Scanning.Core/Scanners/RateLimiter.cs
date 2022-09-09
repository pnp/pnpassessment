using PnP.Core.Services;
using Serilog;

namespace PnP.Scanning.Core.Scanners
{
    /// <summary>
    /// Rate limiter class, will delay outgoing requests based upon ratelimit headers received from previous requests. 
    /// Goal of the delaying is to prevent getting throttled, resulting in a better overall throughput
    /// </summary>
    internal sealed class RateLimiter
    {
        internal const string RATELIMIT_LIMIT = "RateLimit-Limit";
        internal const string RATELIMIT_REMAINING = "RateLimit-Remaining";
        internal const string RATELIMIT_RESET = "RateLimit-Reset";

        /// <summary>
        /// Lock for controlling Read/Write access to the variables.
        /// </summary>
        private readonly ReaderWriterLockSlim readerWriterLock = new ReaderWriterLockSlim();

        /// <summary>
        /// Maximum number of requests per window
        /// </summary>
        private int limit;

        /// <summary>
        /// The time, in <see cref="TimeSpan.Seconds"/>, when the current window gets reset
        /// </summary>
        private int reset;

        /// <summary>
        /// The timestamp when current window will be reset, in <see cref="TimeSpan.Ticks"/>.
        /// </summary>
        private long nextReset;

        /// <summary>
        /// The remaining requests in the current window.
        /// </summary>
        private int remaining;

        /// <summary>
        /// Minimum % of requests left before the next request will get delayed until the current window is reset.
        /// </summary>
        private int minimumCapacityLeft = 10;

        /// <summary>
        /// Default constructor
        /// </summary>
        public RateLimiter()
        {

            readerWriterLock.EnterWriteLock();
            try
            {
                _ = Interlocked.Exchange(ref limit, -1);
                _ = Interlocked.Exchange(ref remaining, -1);
                _ = Interlocked.Exchange(ref reset, -1);
                _ = Interlocked.Exchange(ref nextReset, DateTime.UtcNow.Ticks);
            }
            finally
            {
                readerWriterLock.ExitWriteLock();
            }
        }

        internal async Task WaitAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // We're not using the rate limiter
            if (minimumCapacityLeft == 0)
            {
                return;
            }

            long delayInTicks = 0;
            float capacityLeft = 0;
            readerWriterLock.EnterReadLock();
            try
            {
                // Remaining = 0 means the request is throttled and there's a retry-after header that will be used
                if (limit > 0 && remaining > 0)
                {
                    // Calculate percentage requests left in the current window
                    capacityLeft = ((float)remaining / limit) * 100;

                    // If getting below the minimum required capacity then lets wait until the current window is reset
                    if (capacityLeft <= minimumCapacityLeft)
                    {
                        delayInTicks = nextReset - DateTime.UtcNow.Ticks;
                    }
                }
            }
            finally
            {
                readerWriterLock.ExitReadLock();
            }

            if (delayInTicks > 0)
            {
                Log.Information("Delaying request for {RequestDelay} seconds because remaining request capacity for the current window is at {CapacityLeft}, below the {MinimumCapacityLeft} threshold.", new TimeSpan(delayInTicks).Seconds, capacityLeft, minimumCapacityLeft);
                await Task.Delay(new TimeSpan(delayInTicks), cancellationToken).ConfigureAwait(false);
            }
        }

        internal void UpdateWindow(IRateLimitEvent rateLimitEvent)
        {
            // We're not using the rate limiter
            if (minimumCapacityLeft == 0)
            {
                return;
            }

            readerWriterLock.EnterWriteLock();
            try
            {
                _ = Interlocked.Exchange(ref limit, rateLimitEvent.Limit);
                _ = Interlocked.Exchange(ref remaining, rateLimitEvent.Remaining);
                _ = Interlocked.Exchange(ref reset, rateLimitEvent.Reset);

                if (rateLimitEvent.Reset > -1)
                {
                    // Track when the current window get's reset
                    _ = Interlocked.Exchange(ref nextReset, DateTime.UtcNow.Ticks + TimeSpan.FromSeconds(rateLimitEvent.Reset).Ticks);
                }
            }
            finally
            {
                readerWriterLock.ExitWriteLock();
            }
        }

    }
}
