using PnP.Scanning.Core.Discovery;
using System.Net.Http;

namespace PnP.Scanning.Core.Scanners;

/// <summary>
/// A bounded retry for read-only assessment metadata initialization.
/// HttpClient response-body failures can occur outside the SDK's HTTP retry handler.
/// </summary>
internal static class ClassicAssessmentInitialization
{
    internal const int MaxAttempts = 3;

    internal static async Task<T> ExecuteAsync<T>(Func<Task<T>> initialize,
        AssessmentDiscoveryWriter writer, Guid scanId, string siteUrl, string webUrl,
        CancellationToken cancellationToken, Action<int, Exception> onRetry = null,
        Func<TimeSpan, CancellationToken, Task> delay = null,
        Func<Exception, Task> beforeFailureFinalization = null)
    {
        try
        {
            return await RetryAsync(initialize, cancellationToken, onRetry, delay).ConfigureAwait(false);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            // A shared metadata load can fail before discovery has run. Retain its raw
            // inventory first, then give those pages the same failed-assessment disposition.
            if (beforeFailureFinalization != null)
                await beforeFailureFinalization(error).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await writer.FailUnassessedPagesAsync(scanId, siteUrl, webUrl, error,
                "AssessmentInitialization").ConfigureAwait(false);
            throw;
        }
    }

    internal static async Task<T> RetryAsync<T>(Func<Task<T>> initialize, CancellationToken cancellationToken,
        Action<int, Exception> onRetry = null, Func<TimeSpan, CancellationToken, Task> delay = null)
    {
        delay ??= Task.Delay;
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return await initialize().ConfigureAwait(false); }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested &&
                attempt < MaxAttempts && IsResponseEnded(error))
            {
                onRetry?.Invoke(attempt, error);
                await delay(TimeSpan.FromSeconds(attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal static bool IsResponseEnded(Exception error)
    {
        // Do not turn permission/configuration errors into retries. Only the specific
        // response-body interruption observed in the tenant run is retried here.
        if (error is OperationCanceledException) return false;
        if (error is HttpRequestException { StatusCode: not null }) return false;
        return error is HttpRequestException { HttpRequestError: HttpRequestError.ResponseEnded }
            || error is HttpIOException { HttpRequestError: HttpRequestError.ResponseEnded }
            || (error is HttpRequestException && error.InnerException != null && IsResponseEnded(error.InnerException));
    }
}
