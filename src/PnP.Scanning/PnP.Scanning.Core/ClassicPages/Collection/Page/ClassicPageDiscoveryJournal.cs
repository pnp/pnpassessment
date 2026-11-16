using PnP.Scanning.Core.ClassicPages.Collection.Module;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Pipeline.Contracts.Module;

#nullable enable


namespace PnP.Scanning.Core.ClassicPages.Collection.Page;

/// <summary>
/// Wraps discovery requests with durable acquisition receipts for restart.
/// </summary>
internal sealed class ClassicPageDiscoveryJournal(ISharePointAspxRestClientFactory inner, AcquisitionJournal journal)
    : ISharePointAspxRestClientFactory
{
    /// <summary>
    /// Returns a discovery client whose individual requests are durably journaled.
    /// </summary>
    public async Task<ISharePointAspxRestClient> GetAsync(Uri webUrl, CancellationToken cancellationToken = default) =>
        new Client(await inner.GetAsync(webUrl, cancellationToken), journal);

    /// <summary>
    /// Disposes the owning discovery-client factory.
    /// </summary>
    public void Dispose() => inner.Dispose();

    /// <summary>
    /// Journals discovery operations while leaving cached transport ownership with its factory.
    /// </summary>
    /// <summary>
    /// Marks a listing page given up because of throttling as a partial read; every other page keeps its receipt
    /// status.
    /// </summary>
    internal static AcquisitionStatus PageStatus(SharePointRestPage page) =>
        PnPContextSharePointAspxRestClient.IsThrottleFailure(page)
            ? AcquisitionStatus.Partial
            : AcquisitionStatus.Complete;

    /// <summary>
    /// Names the listing request that was given up because of throttling; every other page has no acquisition error.
    /// </summary>
    internal static string? PageError(SharePointRestPage page) =>
        PnPContextSharePointAspxRestClient.IsThrottleFailure(page)
            ? "Throttling stopped a folder listing (" + page.ErrorCode + "): " + page.RequestUri.AbsolutePath
            : null;

    private sealed class Client(ISharePointAspxRestClient inner, AcquisitionJournal journal) : ISharePointAspxRestClient
    {
        public Uri WebUrl => inner.WebUrl;

        /// <summary>
        /// Leaves cached discovery-client disposal to the owning factory.
        /// </summary>
        public void Dispose()
        {
            /* The owning factory owns cached clients. */
        }

        /// <summary>
        /// Combines Web, operation, and locator into a replayable discovery request identity.
        /// </summary>
        private string Key(string operation, string locator) => WebUrl.AbsoluteUri + "|" + operation + "|" + locator;

        /// <summary>
        /// Replays or acquires one original discovery REST page. A page given up because of throttling is recorded
        /// as an incomplete read, so the assessment counts it and names it as the latest issue.
        /// </summary>
        public Task<SharePointRestPage> GetPageAsync(Uri requestUri, CancellationToken cancellationToken = default) =>
            journal.ReadOrAcquireAsync<SharePointRestPage>(
                "DiscoveryRequest",
                Key("page", requestUri.AbsoluteUri),
                () => inner.GetPageAsync(requestUri, cancellationToken),
                cancellationToken,
                PageStatus,
                PageError);

        /// <summary>
        /// Replays or acquires a physical file resolution receipt.
        /// </summary>
        public Task<SharePointResolvedFile> ResolveFileAsync(
            string serverRelativeUrl,
            CancellationToken cancellationToken = default) =>
            journal.ReadOrAcquireAsync(
                "DiscoveryResolvedFile",
                Key("resolve", serverRelativeUrl),
                () => inner.ResolveFileAsync(serverRelativeUrl, cancellationToken),
                cancellationToken);

        /// <summary>
        /// Replays or acquires the modeled welcome-page receipt.
        /// </summary>
        public Task<SharePointModeledValue> ReadWelcomePageAsync(CancellationToken cancellationToken = default) =>
            journal.ReadOrAcquireAsync(
                "DiscoveryWelcomePage",
                Key("welcome", ""),
                () => inner.ReadWelcomePageAsync(cancellationToken),
                cancellationToken);

        /// <summary>
        /// Replays or acquires native folder and physical-child evidence.
        /// </summary>
        public Task<SharePointModeledFolderResult> ReadFolderAsync(
            string serverRelativeUrl,
            CancellationToken cancellationToken = default) =>
            journal.ReadOrAcquireAsync(
                "DiscoveryFolder",
                Key("folder", serverRelativeUrl),
                () => inner.ReadFolderAsync(serverRelativeUrl, cancellationToken),
                cancellationToken);
    }
}