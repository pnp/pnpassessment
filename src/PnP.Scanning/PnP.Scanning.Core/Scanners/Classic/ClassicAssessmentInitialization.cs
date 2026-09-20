using PnP.Scanning.Core.Discovery;
using System.Net.Http;

namespace PnP.Scanning.Core.Scanners;

/// <summary>
/// A bounded retry for the read-only context load between discovery and page assessment.
/// HttpClient response-body failures can occur outside the SDK's HTTP retry handler.
/// </summary>
internal static class ClassicAssessmentInitialization
{
    internal const int MaxAttempts = 3;

    internal static async Task<T> ExecuteAsync<T>(Func<Task<T>> initialize,
        AssessmentDiscoveryWriter writer, Guid scanId, string siteUrl, string webUrl,
        CancellationToken cancellationToken, Action<int, Exception> onRetry = null,
        Func<TimeSpan, CancellationToken, Task> delay = null)
    {
        delay ??= Task.Delay;
        try
        {
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
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            await writer.FailUnassessedPagesAsync(scanId, siteUrl, webUrl, error,
                "AssessmentInitialization").ConfigureAwait(false);
            throw;
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
