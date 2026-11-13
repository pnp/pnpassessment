using System.Collections.Concurrent;
using System.Net.Http.Headers;

namespace PnP.Scanning.Core.Discovery;

/// <summary>Bounds safe JSON retries and supplies a monotonic clock and cancellable delay without changing HTTP transport security.</summary>
internal sealed class SharePointRestThrottlePolicy
{
    internal const int DefaultAttemptLimit = 3;
    private const int MaximumAttemptLimit = 5;
    private const int InitialBackoffSeconds = 1;

    /// <summary>Longest single wait a server may direct before a collection request is given up.</summary>
    internal static readonly TimeSpan CollectionServerWaitLimit = TimeSpan.FromMinutes(10);

    internal static readonly SharePointRestThrottlePolicy Default = new();

    /// <summary>
    /// Shared by every Web of a collection: a request waits as long as the server directs, up to
    /// <see cref="CollectionServerWaitLimit"/> per wait, and every other request to that host holds meanwhile.
    /// </summary>
    internal static readonly SharePointRestThrottlePolicy Collection = new(
        MaximumAttemptLimit,
        serverWaitLimit: CollectionServerWaitLimit);

    /// <summary>
    /// Holds the collection's listing requests to the host of a request the SDK reports as throttled (HTTP 429 or
    /// 503), for the time the SDK waits before it sends that request again. Returns whether the host was held.
    /// </summary>
    internal static bool HoldCollectionFor(PnP.Core.Services.IRetryEvent retry)
    {
        Uri request = retry?.Request;
        if (request == null || !request.IsAbsoluteUri || retry.HttpStatusCode is not (429 or 503) ||
            retry.WaitTime <= 0)
        {
            return false;
        }

        Collection.Hold(request.Authority, TimeSpan.FromSeconds(retry.WaitTime));
        return true;
    }

    /// <summary>
    /// Makes the SDK report its throttled requests to the collection. A listener that is already registered stays
    /// in place, and a session without an event hub has nothing to listen to.
    /// </summary>
    internal static void ListenForThrottling(PnP.Core.Services.EventHub events)
    {
        if (events == null || events.RequestRetry != null) return;
        events.RequestRetry = retry =>
        {
            if (HoldCollectionFor(retry))
            {
                Serilog.Log.Warning(
                    "[Throttling] request {Request} waits {Seconds} seconds and folder listing to its host waits too",
                    retry.Request,
                    retry.WaitTime);
            }
        };
    }

    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly ConcurrentDictionary<string, long> heldUntil = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates a bounded retry policy; clock and delay injection allow deterministic offline transport coverage.
    /// A <paramref name="serverWaitLimit"/> above zero makes the policy wait out throttling.
    /// </summary>
    internal SharePointRestThrottlePolicy(int attemptLimit = DefaultAttemptLimit, TimeProvider clock = null,
        Func<TimeSpan, CancellationToken, Task> delay = null, TimeSpan serverWaitLimit = default)
    {
        if (attemptLimit is < 1 or > MaximumAttemptLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptLimit));
        }
        if (serverWaitLimit < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(serverWaitLimit));
        }
        AttemptLimit = attemptLimit;
        Clock = clock ?? TimeProvider.System;
        ServerWaitLimit = serverWaitLimit;
        this.delay = delay ?? ((duration, token) => Task.Delay(duration, token));
    }

    internal int AttemptLimit { get; }
    internal TimeProvider Clock { get; }

    /// <summary>
    /// Longest single server-directed wait this policy honors outside the request budget. Zero keeps every wait
    /// inside the request budget.
    /// </summary>
    internal TimeSpan ServerWaitLimit { get; }

    /// <summary>States whether a throttled request waits as long as the server directs before its next attempt.
    /// </summary>
    internal bool WaitsOutThrottling => ServerWaitLimit > TimeSpan.Zero;

    /// <summary>
    /// Holds every request of this policy to <paramref name="host"/> for <paramref name="duration"/>, at most
    /// <see cref="ServerWaitLimit"/>, keeping the later end when the host is already held. The end is kept on the
    /// monotonic clock. A policy that keeps waits inside the request budget holds nothing.
    /// </summary>
    internal void Hold(string host, TimeSpan duration)
    {
        if (!WaitsOutThrottling || string.IsNullOrEmpty(host) || duration <= TimeSpan.Zero) return;
        TimeSpan bounded = duration < ServerWaitLimit ? duration : ServerWaitLimit;
        long until = Clock.GetTimestamp() +
                     (long)(bounded.Ticks * ((double)Clock.TimestampFrequency / TimeSpan.TicksPerSecond));
        heldUntil.AddOrUpdate(host, until, (_, current) => current > until ? current : until);
    }

    /// <summary>
    /// Returns how long requests to <paramref name="host"/> still hold; zero means a request may be sent.
    /// </summary>
    internal TimeSpan HoldRemaining(string host)
    {
        if (string.IsNullOrEmpty(host) || !heldUntil.TryGetValue(host, out long until)) return TimeSpan.Zero;
        long now = Clock.GetTimestamp();
        return until > now ? Clock.GetElapsedTime(now, until) : TimeSpan.Zero;
    }

    /// <summary>Honors a server delta or HTTP-date without clamping; unusable supplied delays fail closed against the overall budget.</summary>
    internal TimeSpan GetRetryDelay(HttpResponseMessage response, DateTimeOffset receivedAtUtc, int attemptCount)
    {
        if (response.Headers.TryGetValues("Retry-After", out var values))
        {
            var supplied = values.ToArray();
            if (supplied.Length != 1 || !RetryConditionHeaderValue.TryParse(supplied[0], out var retryAfter))
            {
                return TimeSpan.MaxValue;
            }
            if (retryAfter.Delta.HasValue)
            {
                return retryAfter.Delta.Value;
            }
            var remaining = retryAfter.Date.Value - receivedAtUtc;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
        return TimeSpan.FromSeconds(InitialBackoffSeconds * (1 << (attemptCount - 1)));
    }

    /// <summary>Waits at least the requested monotonic duration, propagating cancellation even when an injected delay ignores its token.</summary>
    internal async Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var started = Clock.GetTimestamp();
        var remaining = duration;
        while (remaining > TimeSpan.Zero)
        {
            await delay(remaining, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            remaining = duration - Clock.GetElapsedTime(started);
        }
    }
}
