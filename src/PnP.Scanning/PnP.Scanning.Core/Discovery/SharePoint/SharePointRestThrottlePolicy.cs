using System.Net.Http.Headers;

namespace PnP.Scanning.Core.Discovery;

/// <summary>Bounds safe JSON retries and supplies a monotonic clock and cancellable delay without changing HTTP transport security.</summary>
internal sealed class SharePointRestThrottlePolicy
{
    internal const int DefaultAttemptLimit = 3;
    private const int MaximumAttemptLimit = 5;
    private const int InitialBackoffSeconds = 1;

    internal static readonly SharePointRestThrottlePolicy Default = new();
    private readonly Func<TimeSpan, CancellationToken, Task> delay;

    /// <summary>Creates a bounded retry policy; clock and delay injection allow deterministic offline transport coverage.</summary>
    internal SharePointRestThrottlePolicy(int attemptLimit = DefaultAttemptLimit, TimeProvider clock = null,
        Func<TimeSpan, CancellationToken, Task> delay = null)
    {
        if (attemptLimit is < 1 or > MaximumAttemptLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptLimit));
        }
        AttemptLimit = attemptLimit;
        Clock = clock ?? TimeProvider.System;
        this.delay = delay ?? ((duration, token) => Task.Delay(duration, token));
    }

    internal int AttemptLimit { get; }
    internal TimeProvider Clock { get; }

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
