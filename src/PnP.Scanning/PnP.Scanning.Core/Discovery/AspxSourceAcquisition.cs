using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.Discovery;

/// <summary>
/// Adapts reusable reads to the inherited family callback before list-item admission.
/// Source failures remain per-file observations, not whole-page assessment skips.
/// </summary>
internal static class AspxSourceAcquisition
{
    internal static Func<ClassicPageDiscovery, CancellationToken, Task> ForScan(Scan scan,
        Func<ClassicPageDiscovery, CancellationToken, Task<AspxSourceReadResult>> readSource) =>
        ForScan(scan, readSource, null);

    internal static Func<ClassicPageDiscovery, CancellationToken, Task> ForScan(Scan scan,
        Func<ClassicPageDiscovery, CancellationToken, Task<AspxSourceReadResult>> readSource,
        PageBaseTypeConfiguration configuration) => Create(scan, readSource, configuration,
            scan.PublishingLayoutRuleVersion == PublishingLayoutTypeCatalog.CurrentRuleVersion);

    /// <summary>Production restart uses only the recorded scan contract, never current mutable settings.</summary>
    internal static Func<ClassicPageDiscovery, CancellationToken, Task> ForAssessmentScan(Scan scan,
        Func<ClassicPageDiscovery, CancellationToken, Task<AspxSourceReadResult>> readSource) =>
        Create(scan, readSource, PageBaseTypeConfiguration.FromJson(scan.PageBaseTypeConfigurationJson),
            scan.PageSourceEvidenceVersion == ClassicSourceEvidence.CurrentVersion);

    private static Func<ClassicPageDiscovery, CancellationToken, Task> Create(Scan scan,
        Func<ClassicPageDiscovery, CancellationToken, Task<AspxSourceReadResult>> readSource,
        PageBaseTypeConfiguration configuration, bool projectBaseType)
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
            var projection = projectBaseType ? PageBaseTypeProjection.Inspect(result, configuration) : null;
            if (projection != null) row.RecordBaseTypeProjection(projection);
            if (catalog != null)
            {
                // Parse once. Defaults never feed the inherited declaration/family predicate.
                PublishingLayoutTypeEvidence.ApplySourceRead(row, result, catalog,
                    projection?.Parse ?? PageDirectiveParser.Parse(result.DecodedText));
            }
        };
    }
}
