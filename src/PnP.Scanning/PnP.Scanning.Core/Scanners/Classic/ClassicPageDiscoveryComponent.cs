using PnP.Core.Auth;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Services;

namespace PnP.Scanning.Core.Scanners;

internal static class ClassicPageDiscoveryComponent
{
    internal static async Task<List<ClassicPageDiscovery>> ExecuteAsync(ClassicScanner scanner)
    {
        var token = scanner.ScanManager.GetCancellationTokenSource(scanner.ScanId).Token;
        var writer = new AssessmentDiscoveryWriter(scanner.ScanId);
        // Use the existing assessment authentication, ScanId and scheduled Web scope.
        var auth = new ExternalAuthenticationProvider((_, scopes) =>
            scanner.ScanManager.GetScanAuthenticationManager(scanner.ScanId).GetAccessTokenAsync(scopes));
        using var context = await scanner.GetPnPContextAsync().ConfigureAwait(false);
        var site = await context.Site.GetAsync(value => value.Id).ConfigureAwait(false);
        var web = await context.Web.GetAsync(value => value.Id, value => value.Url,
            value => value.ServerRelativeUrl).ConfigureAwait(false);
        using var factory = new PnPContextSharePointAspxRestClientFactory(scanner.PnPContextFactory, auth, scanner.ScanId);
        using var provider = new SharePointLiveAspxDiscoveryProvider(new(
            "assessment:" + scanner.ScanId.ToString("D"), "scheduled-web", "classic-assessment",
            DiscoveryHash.Of("classic-assessment", scanner.ScanId.ToString("D"), site.Id.ToString("D"),
                web.Id.ToString("D"))), factory,
            new AspxWebAcquisitionContext(site.Id, new Uri(scanner.SiteUrl), web.Id, web.Url,
                web.ServerRelativeUrl, scanner.WebTemplate));
        await new AssessmentWebDiscovery(scanner.ScanId, scanner.SiteUrl, scanner.WebUrl, writer)
            .RunAsync(provider, token).ConfigureAwait(false);
        return await writer.ReadPagesAsync(scanner.ScanId, scanner.SiteUrl, scanner.WebUrl, token).ConfigureAwait(false);
    }

    internal static Task RecordWebEnumerationScopeAsync(Guid scanId, string siteUrl,
        WebEnumerationResult enumeration, Exception error = null, AssessmentDiscoveryWriter writer = null)
    {
        // The checkpoint contains only pending work. Do not replace the original authority's
        // child counts (or a retained denial/failure) with the size of this restart queue.
        if (enumeration.IsCheckpointReplay && error == null) return Task.CompletedTask;
        return RecordScopeAsync(scanId, siteUrl, "", "SiteCollection",
            error == null ? "Complete" : AssessmentWebDiscovery.Status(AssessmentWebDiscovery.Classify(error)),
            error, children: error == null ? enumeration.Webs.Count : null, stage: "EnumerateWebs", writer: writer);
    }

    internal static Task RecordScopeAsync(Guid scanId, string siteUrl, string webUrl, string scopeType,
        string status, Exception error = null, int? children = null, string stage = "Discovery",
        AssessmentDiscoveryWriter writer = null)
    {
        var row = new ClassicPageDiscovery
        {
            ScanId = scanId, SiteUrl = siteUrl, WebUrl = webUrl,
            RecordKey = "assessment:" + DiscoveryHash.Of(scopeType, siteUrl?.ToLowerInvariant(), webUrl?.ToLowerInvariant(), stage),
            RowType = "Scope", ScopeType = scopeType, Url = siteUrl?.TrimEnd('/') + webUrl,
            DiscoveryStatus = status, ExpectedChildCount = children, ObservedChildCount = children,
            ObservationMethod = stage, ObservedAtUtc = DateTime.UtcNow,
        };
        if (error != null) AssessmentWebDiscovery.AddError(row, stage,
            AssessmentWebDiscovery.ErrorCode(error), AssessmentWebDiscovery.ErrorDetail(error));
        return (writer ?? new AssessmentDiscoveryWriter(scanId)).WriteAsync(new[] { row });
    }
}
