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
        // Use the native run's authentication. No second login, CLI, registry or artifact run id.
        var auth = new ExternalAuthenticationProvider((_, scopes) =>
            scanner.ScanManager.GetScanAuthenticationManager(scanner.ScanId).GetAccessTokenAsync(scopes));
        using var context = await scanner.GetPnPContextAsync().ConfigureAwait(false);
        var site = await context.Site.GetAsync(value => value.Id).ConfigureAwait(false);
        var web = await context.Web.GetAsync(value => value.Id, value => value.Url,
            value => value.ServerRelativeUrl).ConfigureAwait(false);
        var owner = new AspxAuthoritySite(site.Id, Guid.Empty, new Uri(scanner.SiteUrl), null, null);
        var current = new AspxAuthorityWeb(web.Id, web.Url, web.ServerRelativeUrl, null, scanner.WebTemplate,
            string.IsNullOrEmpty(scanner.WebUrl.Trim('/')));
        using var factory = new PnPContextSharePointAspxRestClientFactory(scanner.PnPContextFactory, auth, scanner.ScanId);
        using var provider = new SharePointLiveAspxDiscoveryProvider(new(new[] { owner.Url },
            "native-assessment:" + scanner.ScanId.ToString("D"), "scheduled-web", "native-scan", string.Empty),
            factory, owner, current);
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
            RecordKey = "native:" + DiscoveryHash.Of(scopeType, siteUrl?.ToLowerInvariant(), webUrl?.ToLowerInvariant(), stage),
            RowType = "Scope", ScopeType = scopeType, Url = siteUrl?.TrimEnd('/') + webUrl,
            DiscoveryStatus = status, ExpectedChildCount = children, ObservedChildCount = children,
            ObservationMethod = stage, ObservedAtUtc = DateTime.UtcNow,
        };
        if (error != null) AssessmentWebDiscovery.AddError(row, stage,
            AssessmentWebDiscovery.ErrorCode(error), AssessmentWebDiscovery.ErrorDetail(error));
        return (writer ?? new AssessmentDiscoveryWriter(scanId)).WriteAsync(new[] { row });
    }
}
