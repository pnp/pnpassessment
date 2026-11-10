using PnP.Scanning.Core.ClassicPages.Collection.Discovery;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Services;

namespace PnP.Scanning.Core.ClassicPages.Compatibility;

/// <summary>Adapts retained legacy queue scope observations to discovery persistence.</summary>
internal static class LegacyScopeEvidence
{
    /// <summary>Persists original web-enumeration authority without replacing retained evidence with the pending checkpoint replay queue.</summary>
    internal static async Task RecordWebEnumerationScopeAsync(
        Guid scanId,
        string siteUrl,
        WebEnumerationResult enumeration,
        Exception error = null,
        AssessmentDiscoveryWriter writer = null)
    {
        // The checkpoint contains only pending work. Do not replace the original authority's
        // child counts (or a retained denial/failure) with the size of this restart queue.
        if (enumeration.IsCheckpointReplay && error == null)
        {
            return;
        }

        AssessmentDiscoveryWriter sink = writer ?? new AssessmentDiscoveryWriter(scanId);
        if (enumeration.ScopeEvidence is { Count: > 0 })
            await sink.WriteAsync(enumeration.ScopeEvidence).ConfigureAwait(false);
        await RecordScopeAsync(
                scanId,
                siteUrl,
                "",
                "SiteCollection",
                error == null ? "Complete" : AssessmentWebDiscovery.Status(AssessmentWebDiscovery.Classify(error)),
                error,
                children: error == null ? enumeration.Webs.Count : null,
                stage: "EnumerateWebs",
                writer: sink)
            .ConfigureAwait(false);
    }

    /// <summary>Creates one durable scope observation with optional failure evidence and writes it through the assessment sink.</summary>
    internal static Task RecordScopeAsync(
        Guid scanId,
        string siteUrl,
        string webUrl,
        string scopeType,
        string status,
        Exception error = null,
        int? children = null,
        string stage = "Discovery",
        AssessmentDiscoveryWriter writer = null)
    {
        ClassicPageDiscovery row = new ClassicPageDiscovery
        {
            ScanId = scanId,
            SiteUrl = siteUrl,
            WebUrl = webUrl,
            RecordKey =
                "assessment:" + DiscoveryHash.Of(
                    scopeType,
                    siteUrl?.ToLowerInvariant(),
                    webUrl?.ToLowerInvariant(),
                    stage),
            RowType = "Scope",
            ScopeType = scopeType,
            Url = siteUrl?.TrimEnd('/') + webUrl,
            DiscoveryStatus = status,
            ExpectedChildCount = children,
            ObservedChildCount = children,
            ObservationMethod = stage,
            ObservedAtUtc = DateTime.UtcNow,
        };
        if (error != null)
            AssessmentWebDiscovery.AddError(
                row,
                stage,
                AssessmentWebDiscovery.ErrorCode(error),
                AssessmentWebDiscovery.ErrorDetail(error));
        return (writer ?? new AssessmentDiscoveryWriter(scanId)).WriteAsync(new[] { row });
    }
}