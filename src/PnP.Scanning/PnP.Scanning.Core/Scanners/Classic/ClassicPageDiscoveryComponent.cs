using PnP.Core.Auth;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Discovery.Aspx;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Services;

namespace PnP.Scanning.Core.Scanners;

internal static class ClassicPageDiscoveryComponent
{
    /// <summary>Owns a temporary web context while discovering ASPX files for the scheduled web.</summary>
    internal static async Task<List<ClassicPageDiscovery>> ExecuteAsync(ClassicScanner scanner)
    {
        using var context = await scanner.GetPnPContextAsync().ConfigureAwait(false);
        return await ExecuteAsync(scanner, context).ConfigureAwait(false);
    }

    /// <summary>Uses the shared web context to persist physical ASPX discovery before scan-wide acquisition begins.</summary>
    internal static async Task<List<ClassicPageDiscovery>> ExecuteAsync(ClassicScanner scanner,
        PnP.Core.Services.PnPContext context, SharePointListMetadataSnapshot sharedLists = null)
    {
        var token = scanner.ScanManager.GetCancellationTokenSource(scanner.ScanId).Token;
        var writer = new AssessmentDiscoveryWriter(scanner.ScanId);
        var auth = new ExternalAuthenticationProvider((_, scopes) =>
            scanner.ScanManager.GetScanAuthenticationManager(scanner.ScanId).GetAccessTokenAsync(scopes));
        // Context creation already loaded Site.Id and Web.Id/Url. Ensure only what is missing.
        await context.Site.EnsurePropertiesAsync(value => value.Id, value => value.Url).ConfigureAwait(false);
        await context.Web.EnsurePropertiesAsync(value => value.Id, value => value.Url,
            value => value.ServerRelativeUrl).ConfigureAwait(false);
        var site = context.Site;
        var web = context.Web;
        using var factory = new PnPContextSharePointAspxRestClientFactory(scanner.PnPContextFactory, auth,
            scanner.ScanId, sharedContext: context);
        using var provider = new SharePointLiveAspxDiscoveryProvider(new(
            "assessment:" + scanner.ScanId.ToString("D"), "scheduled-web", "classic-assessment",
            DiscoveryHash.Of("classic-assessment", scanner.ScanId.ToString("D"), site.Id.ToString("D"),
                web.Id.ToString("D")), Intent: scanner.Options.HomePageOnly
                ? AspxDiscoveryIntent.HomePageOnly : AspxDiscoveryIntent.FullInventory), factory,
            new AspxWebAcquisitionContext(site.Id, new Uri(scanner.SiteUrl), web.Id, web.Url,
                web.ServerRelativeUrl, scanner.WebTemplate), sharedLists);
        await new AssessmentWebDiscovery(scanner.ScanId, scanner.SiteUrl, scanner.WebUrl, writer)
            .RunAsync(provider, token).ConfigureAwait(false);
        return await writer.ReadPagesAsync(scanner.ScanId, scanner.SiteUrl, scanner.WebUrl, token).ConfigureAwait(false);
    }

    /// <summary>Reads all persisted Web roots after discovery workers finish, then enriches their ASPX observations.</summary>
    internal static async Task<int> EnrichScanAsync(ClassicScanner scanner)
    {
        var token = scanner.ScanManager.GetCancellationTokenSource(scanner.ScanId).Token;
        var writer = new AssessmentDiscoveryWriter(scanner.ScanId);
        var projection = new ClassicAspxProjectionWriter(scanner.ScanId);
        var catalog = scanner.Options.PlatformAspxCatalogPayload == null ? null :
            PlatformAspxCatalog.FromFrozenPayload(scanner.Options.PlatformAspxCatalogPayload);
        var auth = new ExternalAuthenticationProvider((_, scopes) =>
            scanner.ScanManager.GetScanAuthenticationManager(scanner.ScanId).GetAccessTokenAsync(scopes));
        var failures = 0;
        await foreach (var web in writer.ReadDiscoveredWebScopesAsync(scanner.ScanId, token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var factory = new PnPContextSharePointAspxRestClientFactory(scanner.PnPContextFactory, auth,
                    scanner.ScanId);
                var webUri = new Uri(web.Url, UriKind.Absolute);
                var client = await factory.GetAsync(webUri, token).ConfigureAwait(false);
                var acquisitionContext = new ClassicAspxWebAcquisitionContext(scanner.ScanId,
                    web.SiteCollectionId.Value, web.WebId.Value, new Uri(web.SiteUrl, UriKind.Absolute),
                    webUri, scanner.Options, catalog);
                await new ClassicAspxWebAcquisition(acquisitionContext, client, factory).AcquireAsync(
                    writer.ReadPageStreamAsync(scanner.ScanId, web.SiteUrl, web.WebUrl, token),
                    async (batch, cancellation) =>
                    {
                        // Keep raw acquisition evidence even if a later projection write fails.
                        foreach (var evidence in batch.Evidence)
                        {
                            evidence.SiteUrl = web.SiteUrl;
                            evidence.WebUrl = web.WebUrl;
                        }
                        if (batch.Evidence.Count != 0)
                            await writer.WriteAsync(batch.Evidence, cancellation).ConfigureAwait(false);
                        await projection.WriteAsync(batch.Inventory, batch.Bindings, cancellation).ConfigureAwait(false);
                    }, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !token.IsCancellationRequested)
            {
                failures++;
                await RecordScopeAsync(scanner.ScanId, web.SiteUrl, web.WebUrl, "Web", "Failed", ex,
                    stage: "AspxAcquisition", writer: writer).ConfigureAwait(false);
                scanner.Logger.Warning(ex, "ASPX acquisition failed for web {SiteUrl}{WebUrl}",
                    web.SiteUrl, web.WebUrl);
            }
        }
        return failures;
    }

    /// <summary>Persists original web-enumeration authority without replacing retained evidence with the pending checkpoint replay queue.</summary>
    internal static async Task RecordWebEnumerationScopeAsync(Guid scanId, string siteUrl,
        WebEnumerationResult enumeration, Exception error = null, AssessmentDiscoveryWriter writer = null)
    {
        // The checkpoint contains only pending work. Do not replace the original authority's
        // child counts (or a retained denial/failure) with the size of this restart queue.
        if (enumeration.IsCheckpointReplay && error == null)
        {
            return;
        }
        var sink = writer ?? new AssessmentDiscoveryWriter(scanId);
        if (enumeration.ScopeEvidence is { Count: > 0 })
            await sink.WriteAsync(enumeration.ScopeEvidence).ConfigureAwait(false);
        await RecordScopeAsync(scanId, siteUrl, "", "SiteCollection",
            error == null ? "Complete" : AssessmentWebDiscovery.Status(AssessmentWebDiscovery.Classify(error)),
            error, children: error == null ? enumeration.Webs.Count : null, stage: "EnumerateWebs", writer: sink).ConfigureAwait(false);
    }

    /// <summary>Creates one durable scope observation with optional failure evidence and writes it through the assessment sink.</summary>
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
