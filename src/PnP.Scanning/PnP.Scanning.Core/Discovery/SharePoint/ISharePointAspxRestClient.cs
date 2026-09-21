namespace PnP.Scanning.Core.Discovery;

internal interface ISharePointAspxRestClient : IDisposable
{
    Uri WebUrl { get; }
    Task<SharePointRestPage> GetPageAsync(Uri requestUri, CancellationToken cancellationToken = default);
    Task<SharePointResolvedFile> ResolveFileAsync(string serverRelativeUrl,
        CancellationToken cancellationToken = default);
    Task<SharePointModeledValue> ReadWelcomePageAsync(CancellationToken cancellationToken = default);
    Task<SharePointModeledFolderResult> ReadFolderAsync(string serverRelativeUrl,
        CancellationToken cancellationToken = default);
}
