using PnP.Scanning.Core.Pipeline.Contracts.Page;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;

namespace PnP.Scanning.Core.Storage.Pipeline;

/// <summary>
/// Bridges typed snapshot evidence and the unchanged legacy string discriminator without altering row data or database schema.
/// </summary>
internal static class DiscoveryEvidenceMapper
{
    /// <summary>
    /// Copies legacy acquisition evidence through the strict text converter, rejecting missing or unsupported discriminator values.
    /// </summary>
    internal static DiscoveryEvidence FromLegacy(ClassicPageDiscovery row) =>
        CollectionJson.Convert<DiscoveryEvidence>(row);

    /// <summary>
    /// Restores established RowType text and all original evidence fields for existing storage and report consumers.
    /// </summary>
    internal static ClassicPageDiscovery ToLegacy(DiscoveryEvidence evidence) =>
        CollectionJson.Convert<ClassicPageDiscovery>(evidence);
}