namespace PnP.Scanning.Core.Discovery;

internal interface ISharePointAspxRestClient : IDisposable
{
    Uri WebUrl { get; }

    /// <summary>Reads one authenticated REST page while retaining response, pagination, and terminal-outcome evidence.</summary>
    Task<SharePointRestPage> GetPageAsync(Uri requestUri, CancellationToken cancellationToken = default);

    /// <summary>Resolves a locator to observed physical-file metadata without deriving identity from the path.</summary>
    Task<SharePointResolvedFile> ResolveFileAsync(string serverRelativeUrl,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the modeled welcome-page property with its provider outcome and provenance.</summary>
    Task<SharePointModeledValue> ReadWelcomePageAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads one modeled folder and its expanded child collections without treating an unexpanded surface as empty.</summary>
    Task<SharePointModeledFolderResult> ReadFolderAsync(string serverRelativeUrl,
        CancellationToken cancellationToken = default);
}
