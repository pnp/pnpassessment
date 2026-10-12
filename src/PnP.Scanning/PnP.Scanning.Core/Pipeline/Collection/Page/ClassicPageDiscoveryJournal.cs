#nullable enable
using PnP.Scanning.Core.Pipeline.Collection.Module;
using PnP.Scanning.Core.Discovery;

namespace PnP.Scanning.Core.Pipeline.Collection.Page;

/// <summary>
/// Wraps the ASPX REST client factory so completed discovery reads are persisted and replayed during collection restart.
/// </summary>
internal sealed class ClassicPageDiscoveryJournal(ISharePointAspxRestClientFactory inner,
    ClassicPageAcquisitionJournal journal) : ISharePointAspxRestClientFactory
{
    /// <summary>
    /// Creates a journal-backed client for the requested web using the owning REST client factory.
    /// </summary>
    public async Task<ISharePointAspxRestClient> GetAsync(Uri webUrl, CancellationToken cancellationToken = default) =>
        new Client(await inner.GetAsync(webUrl, cancellationToken), journal);
    /// <summary>
    /// Disposes the owning REST client factory and its cached clients.
    /// </summary>
    public void Dispose() => inner.Dispose();
    /// <summary>
    /// Journals paged REST, file-resolution, welcome-page and modeled folder reads for one web.
    /// </summary>
    private sealed class Client(ISharePointAspxRestClient inner, ClassicPageAcquisitionJournal journal) : ISharePointAspxRestClient
    {
        public Uri WebUrl => inner.WebUrl;
        /// <summary>
        /// Leaves client disposal to the owning factory because the wrapped clients are cached and shared.
        /// </summary>
        public void Dispose() { /* The owning factory owns cached clients. */ }
        /// <summary>
        /// Builds a request identity from the web, operation and locator so different discovery reads cannot collide.
        /// </summary>
        private string Key(string operation, string locator) => WebUrl.AbsoluteUri + "|" + operation + "|" + locator;
        /// <summary>
        /// Returns a committed REST page or saves the newly acquired page and its continuation evidence.
        /// </summary>
        public Task<SharePointRestPage> GetPageAsync(Uri requestUri, CancellationToken cancellationToken = default) =>
            journal.ReadOrAcquireAsync("DiscoveryRequest", Key("page", requestUri.AbsoluteUri),
                () => inner.GetPageAsync(requestUri, cancellationToken), cancellationToken);
        /// <summary>
        /// Replays or persists the owner and identity resolved for a discovered server-relative file.
        /// </summary>
        public Task<SharePointResolvedFile> ResolveFileAsync(string serverRelativeUrl, CancellationToken cancellationToken = default) =>
            journal.ReadOrAcquireAsync("DiscoveryResolvedFile", Key("resolve", serverRelativeUrl),
                () => inner.ResolveFileAsync(serverRelativeUrl, cancellationToken), cancellationToken);
        /// <summary>
        /// Replays or persists the modeled welcome-page value for this web.
        /// </summary>
        public Task<SharePointModeledValue> ReadWelcomePageAsync(CancellationToken cancellationToken = default) =>
            journal.ReadOrAcquireAsync("DiscoveryWelcomePage", Key("welcome", ""),
                () => inner.ReadWelcomePageAsync(cancellationToken), cancellationToken);
        /// <summary>
        /// Replays or persists the modeled folder contents and read status for this locator.
        /// </summary>
        public Task<SharePointModeledFolderResult> ReadFolderAsync(string serverRelativeUrl, CancellationToken cancellationToken = default) =>
            journal.ReadOrAcquireAsync("DiscoveryFolder", Key("folder", serverRelativeUrl),
                () => inner.ReadFolderAsync(serverRelativeUrl, cancellationToken), cancellationToken);
    }
}
