namespace PnP.Scanning.Core.Discovery;

internal interface ISharePointAspxRestClientFactory : IDisposable
{
    Task<ISharePointAspxRestClient> GetAsync(Uri webUrl, CancellationToken cancellationToken = default);
}
