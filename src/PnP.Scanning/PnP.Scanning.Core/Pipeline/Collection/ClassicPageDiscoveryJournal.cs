#nullable enable
using PnP.Scanning.Core.Discovery;

namespace PnP.Scanning.Core.Pipeline.Collection;

/// <summary>Restores the provider's traversal from durable request receipts, without repeating completed requests.</summary>
internal sealed class ClassicPageDiscoveryJournal(ISharePointAspxRestClientFactory inner,
    ClassicPageAcquisitionJournal journal) : ISharePointAspxRestClientFactory
{
    public async Task<ISharePointAspxRestClient> GetAsync(Uri webUrl, CancellationToken cancellationToken = default) =>
        new Client(await inner.GetAsync(webUrl, cancellationToken), journal);
    public void Dispose() => inner.Dispose();
    private sealed class Client(ISharePointAspxRestClient inner, ClassicPageAcquisitionJournal journal) : ISharePointAspxRestClient
    {
        public Uri WebUrl => inner.WebUrl;
        public void Dispose() { /* The owning factory owns cached clients. */ }
        private string Key(string operation, string locator) => WebUrl.AbsoluteUri + "|" + operation + "|" + locator;
        public Task<SharePointRestPage> GetPageAsync(Uri requestUri, CancellationToken cancellationToken = default) =>
            journal.ReadOrAcquireAsync("DiscoveryRequest", Key("page", requestUri.AbsoluteUri),
                () => inner.GetPageAsync(requestUri, cancellationToken), cancellationToken);
        public Task<SharePointResolvedFile> ResolveFileAsync(string serverRelativeUrl, CancellationToken cancellationToken = default) =>
            journal.ReadOrAcquireAsync("DiscoveryResolvedFile", Key("resolve", serverRelativeUrl),
                () => inner.ResolveFileAsync(serverRelativeUrl, cancellationToken), cancellationToken);
        public Task<SharePointModeledValue> ReadWelcomePageAsync(CancellationToken cancellationToken = default) =>
            journal.ReadOrAcquireAsync("DiscoveryWelcomePage", Key("welcome", ""),
                () => inner.ReadWelcomePageAsync(cancellationToken), cancellationToken);
        public Task<SharePointModeledFolderResult> ReadFolderAsync(string serverRelativeUrl, CancellationToken cancellationToken = default) =>
            journal.ReadOrAcquireAsync("DiscoveryFolder", Key("folder", serverRelativeUrl),
                () => inner.ReadFolderAsync(serverRelativeUrl, cancellationToken), cancellationToken);
    }
}
