using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Discovery;
#nullable enable
using PnP.Scanning.Core.Pipeline.Collection.Module;

namespace PnP.Scanning.Core.Pipeline.Collection.Page;

/// <summary>
/// Wraps discovery requests with durable acquisition receipts for restart.
/// </summary>
internal sealed class ClassicPageDiscoveryJournal(ISharePointAspxRestClientFactory inner,
    ClassicPageAcquisitionJournal journal) : ISharePointAspxRestClientFactory
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
    private sealed class Client(ISharePointAspxRestClient inner, ClassicPageAcquisitionJournal journal) : ISharePointAspxRestClient
    {
        public Uri WebUrl => inner.WebUrl;
        /// <summary>
        /// Leaves cached discovery-client disposal to the owning factory.
        /// </summary>
        public void Dispose() { /* The owning factory owns cached clients. */ }
        /// <summary>
        /// Combines Web, operation, and locator into a replayable discovery request identity.
        /// </summary>
        private string Key(string operation, string locator) => WebUrl.AbsoluteUri + "|" + operation + "|" + locator;
        /// <summary>
        /// Replays or acquires one original discovery REST page.
        /// </summary>
        public Task<SharePointRestPage> GetPageAsync(Uri requestUri, CancellationToken cancellationToken = default) =>
            journal.ReadOrAcquireAsync("DiscoveryRequest", Key("page", requestUri.AbsoluteUri),
                () => inner.GetPageAsync(requestUri, cancellationToken), cancellationToken);
        /// <summary>
        /// Replays or acquires a physical file resolution receipt.
        /// </summary>
        public Task<SharePointResolvedFile> ResolveFileAsync(string serverRelativeUrl, CancellationToken cancellationToken = default) =>
            journal.ReadOrAcquireAsync("DiscoveryResolvedFile", Key("resolve", serverRelativeUrl),
                () => inner.ResolveFileAsync(serverRelativeUrl, cancellationToken), cancellationToken);
        /// <summary>
        /// Replays or acquires the modeled welcome-page receipt.
        /// </summary>
        public Task<SharePointModeledValue> ReadWelcomePageAsync(CancellationToken cancellationToken = default) =>
            journal.ReadOrAcquireAsync("DiscoveryWelcomePage", Key("welcome", ""),
                () => inner.ReadWelcomePageAsync(cancellationToken), cancellationToken);
        /// <summary>
        /// Replays or acquires native folder and physical-child evidence.
        /// </summary>
        public Task<SharePointModeledFolderResult> ReadFolderAsync(string serverRelativeUrl, CancellationToken cancellationToken = default) =>
            journal.ReadOrAcquireAsync("DiscoveryFolder", Key("folder", serverRelativeUrl),
                () => inner.ReadFolderAsync(serverRelativeUrl, cancellationToken), cancellationToken);
    }
}
