#nullable enable
using ClassicPage = PnP.Scanning.Core.Pipeline.Contracts.Page.ClassicPageRow;
using ClassicPageDiscovery = PnP.Scanning.Core.Pipeline.Contracts.Page.ClassicPageDiscoveryRow;
using ClassicPageWebPart = PnP.Scanning.Core.Pipeline.Contracts.Page.ClassicPageWebPartRow;
using SiteCollection = PnP.Scanning.Core.Pipeline.Contracts.Site.SiteCollectionRow;
using System.Text.Json;
using PnP.Scanning.Core.Pipeline.Analysis.Audit;
using PnP.Scanning.Core.Pipeline.Analysis.Page;
using PnP.Scanning.Core.Pipeline.Analysis.Site;
using PnP.Scanning.Core.Pipeline.Analysis.Web;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Pipeline.Contracts.Site;
using PnP.Scanning.Core.Pipeline.Contracts.Web;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Pipeline.Analysis.Page.WebPartMapping;


namespace PnP.Scanning.Core.Pipeline.Analysis.Module;

/// <summary>
/// Assembles page results, web and site summaries, discovery coverage and audit usage into deterministic report rows.
/// <para>Evolved from <c>Scanners/Classic/ClassicScanner.cs</c> and <c>Storage/StorageManager.cs</c> to build report projections from fixed offline results instead of mutable scanner tables.</para>
/// </summary>
internal static class ClassicPageReportBuilder
{
    /// <summary>
    /// Reads committed analysis payloads, builds all Classic Pages report projections, and publishes them under the current analysis run.
    /// <para>Evolved from <c>Scanners/Classic/ClassicScanner.cs</c> and <c>Storage/StorageManager.cs</c> to build report projections from fixed offline results instead of mutable scanner tables.</para>
    /// </summary>
    internal static async Task PublishAsync(Guid assessmentId, ClassicPageInputIndex index, WebPartMappingManager mapping,
        IAnalysisResultReader results, IAnalysisReportWriter reports, CancellationToken token)
    {
        var pages = new List<ClassicPage>(); var parts = new List<ClassicPageWebPart>();
        var dispositions = new Dictionary<string, ClassicPageDiscovery>(StringComparer.Ordinal);
        var modern = new List<ClassicPageDiscovery>(); var audit = new List<ClassicAuditPageStats>();
        foreach (var result in await results.ReadAsync(token))
        {
            var payload = result.Payload.Value;
            if (payload.GetProperty("kind").GetString() == "Page")
            {
                var output = payload.GetProperty("output").Deserialize<ClassicPageProjectionResult>()!;
                if (output.Page != null) pages.Add(output.Page);
                parts.AddRange(output.Parts);
                if (output.Discovery != null)
                {
                    var row = output.Discovery;
                    dispositions[ClassicPageSourceJson.WebKey(row.SiteUrl, row.WebUrl) + "|" + row.RecordKey] = row;
                    if (output.Modern) modern.Add(row);
                }
            }
            else if (payload.GetProperty("kind").GetString() == "Blog")
                pages.AddRange(payload.GetProperty("pages").Deserialize<ClassicPage[]>()!);
            else if (payload.GetProperty("kind").GetString() == "AuditPage")
                audit.AddRange(payload.GetProperty("output").Deserialize<ClassicAuditPageStats[]>()!);
        }
        var rows = new List<AnalysisReportRow>();
        // Assign stable keys and per-report ordinals before publishing versioned row payloads.
        void Add<T>(string kind, IEnumerable<T> values, Func<T, string> key)
        {
            var ordinal = 0;
            foreach (var value in values.OrderBy(key, StringComparer.Ordinal))
                rows.Add(new(kind, key(value), ordinal++, VersionedJson.From(value)));
        }
        // Keep physical page identity distinct across site collections and webs.
        string PageKey(ClassicPage page) => ClassicPageSourceJson.WebKey(page.SiteUrl, page.WebUrl) + "|" + page.PageUrl;
        var summaries = index.Webs.Values.Where(x => x.State.Succeeded).Select(web => ClassicWebSummaryBuilder.Build(assessmentId, web,
            pages.Where(p => p.SiteUrl == web.SiteUrl && p.WebUrl == web.WebUrl).ToArray(),
            modern.Count(x => x.SiteUrl == web.SiteUrl && x.WebUrl == web.WebUrl))).ToArray();
        Add("classicpages", pages, PageKey);
        Add("classicpagewebparts", parts, p => ClassicPageSourceJson.WebKey(p.SiteUrl, p.WebUrl) + "|" + p.PageUrl + "|" + p.WebPartIndex.ToString("D8"));
        Add("classicwebsummaries", summaries, p => ClassicPageSourceJson.WebKey(p.SiteUrl, p.WebUrl));
        Add("classicsitesummaries", ClassicSiteSummaryBuilder.Build(assessmentId, summaries), p => p.SiteUrl);
        Add("classicwebpartunique", ClassicWebPartSummaryBuilder.Build(assessmentId, parts, mapping), p => p.WebPartType);
        Add("classicpublishingsitesummaries", ClassicPublishingSiteSummaryBuilder.Build(assessmentId, summaries, pages), x => x.SiteUrl);
        var discoveries = new List<ClassicPageDiscovery>(index.Scope.Evidence);
        foreach (var scope in index.Scope.SiteScopes)
        {
            var row = new ClassicPageDiscovery { ScanId = assessmentId, SiteUrl = scope.SiteUrl, WebUrl = "",
                RecordKey = "assessment:" + DiscoveryHash.Of("SiteCollection", scope.SiteUrl.ToLowerInvariant(), "", "EnumerateWebs"),
                RowType = "Scope", ScopeType = "SiteCollection", Url = scope.SiteUrl, DiscoveryStatus = scope.State.Status,
                ExpectedChildCount = scope.State.Succeeded ? scope.WebUrls.Length : null, ObservedChildCount = scope.State.Succeeded ? scope.WebUrls.Length : null,
                ObservationMethod = "EnumerateWebs", ObservedAtUtc = index.Scope.AuditWindowEnd };
            if (scope.State.Error != null) ClassicPageProjection.AddError(row, "EnumerateWebs", scope.State);
            discoveries.Add(row);
        }
        discoveries.AddRange(index.Discovery.Select(row => dispositions.GetValueOrDefault(ClassicPageSourceJson.WebKey(row.SiteUrl, row.WebUrl) + "|" + row.RecordKey) ?? row));
        foreach (var web in index.Webs.Values.Where(x => !x.State.Succeeded))
        {
            var row = new ClassicPageDiscovery { ScanId = assessmentId, SiteUrl = web.SiteUrl, WebUrl = web.WebUrl,
                RecordKey = "assessment:" + DiscoveryHash.Of("Web", web.SiteUrl.ToLowerInvariant(), web.WebUrl.ToLowerInvariant(), "WebInitialization"),
                RowType = "Scope", ScopeType = "Web", Url = web.SiteUrl.TrimEnd('/') + web.WebUrl,
                DiscoveryStatus = web.State.Status, ObservationMethod = "WebInitialization", ObservedAtUtc = index.Scope.AuditWindowEnd };
            ClassicPageProjection.AddError(row, "WebInitialization", web.State); discoveries.Add(row);
        }
        discoveries.Add(ClassicPageCoverage.Create(assessmentId, discoveries, index.Scope.AuditWindowEnd));
        Add("discovery", discoveries, x => x.RowType + "|" + ClassicPageSourceJson.WebKey(x.SiteUrl ?? "", x.WebUrl ?? "") + "|" + x.RecordKey);
        Add("sitecollections", index.Scope.SiteScopes.Select(x => new SiteCollection { ScanId = assessmentId, SiteUrl = x.SiteUrl,
            Status = x.State.Succeeded ? ClassicPageSiteStatus.Finished : ClassicPageSiteStatus.Failed, Error = x.State.Error }), x => x.SiteUrl);
        Add("webs", index.Webs.Values.Select(x => new WebRow { ScanId = assessmentId, SiteUrl = x.SiteUrl, WebUrl = x.WebUrl,
            WebUrlAbsolute = x.SiteUrl.TrimEnd('/') + (x.WebUrl == "/" ? "" : x.WebUrl), Template = x.Template,
            Status = x.State.Succeeded ? ClassicPageSiteStatus.Finished : ClassicPageSiteStatus.Failed, Error = x.State.Error }), x => ClassicPageSourceJson.WebKey(x.SiteUrl, x.WebUrl));
        if (!index.Scope.Options.SkipUsageInformation)
            Add("classicpageauditusage", ClassicPageAuditReportBuilder.Build(assessmentId, index.Scope, index.AuditChunks.Values.ToArray(), audit), x => x.SiteUrl + "|" + x.PageUrl);
        await reports.PublishAsync(rows, token);
    }
}
