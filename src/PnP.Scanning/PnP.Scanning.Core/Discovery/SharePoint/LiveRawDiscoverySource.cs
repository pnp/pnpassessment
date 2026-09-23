namespace PnP.Scanning.Core.Discovery;

internal sealed class LiveRawDiscoverySource : IRawDiscoverySource
{
    private readonly Func<CancellationToken, IAsyncEnumerable<RawDiscoveryBatch>> factory;
    internal LiveRawDiscoverySource(Func<CancellationToken, IAsyncEnumerable<RawDiscoveryBatch>> factory) =>
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public IAsyncEnumerable<RawDiscoveryBatch> ReadBatchesAsync(CancellationToken cancellationToken = default) =>
        factory(cancellationToken);
}
