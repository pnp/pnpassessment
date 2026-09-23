namespace PnP.Scanning.Core.Discovery;

internal interface IRawDiscoverySource
{
    IAsyncEnumerable<RawDiscoveryBatch> ReadBatchesAsync(CancellationToken cancellationToken = default);
}
