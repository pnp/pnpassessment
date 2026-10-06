using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.Discovery;

/// <summary>
/// Adapts reusable reads to the inherited family callback before list-item admission.
/// Source failures remain per-file observations, not whole-page assessment skips.
/// </summary>
internal static class AspxSourceAcquisition
{
    internal static Func<ClassicPageDiscovery, CancellationToken, Task> ForScan(Scan scan,
        Func<ClassicPageDiscovery, CancellationToken, Task<AspxSourceReadResult>> readSource)
    {
        var catalog = scan.PublishingLayoutRuleVersion == PublishingLayoutTypeCatalog.CurrentRuleVersion
            ? PublishingLayoutTypeCatalog.FromJson(scan.PublishingLayoutTypeCatalogJson) : null;
        return async (row, token) =>
        {
            token.ThrowIfCancellationRequested();
            var discovery = row.DiscoveryObservation ?? AspxFileObservation.FromDiscovery(row);
            AspxSourceReadResult result;
            try
            {
                result = await readSource(row, token).ConfigureAwait(false) ??
                    AspxSourceReadResult.Unavailable(discovery, AspxSourceTransportState.NotReturned, "ReadResultNotReturned");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                token.ThrowIfCancellationRequested();
                result = AspxSourceReadResult.Unavailable(discovery,
                    AspxSourceReader.IsDenied(ex) ? AspxSourceTransportState.Denied : AspxSourceTransportState.Failed,
                    AssessmentWebDiscovery.ErrorCode(ex) + ":" + ex.Message, httpStatusCode: AspxSourceReader.HttpStatus(ex));
            }
            token.ThrowIfCancellationRequested();
            row.RecordSourceRead(result);
            if (catalog != null) PublishingLayoutTypeEvidence.ApplySourceRead(row, result, catalog);
        };
    }
}
