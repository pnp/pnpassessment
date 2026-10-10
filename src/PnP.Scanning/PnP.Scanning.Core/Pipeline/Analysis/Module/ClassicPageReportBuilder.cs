#nullable enable
using ClassicPage = PnP.Scanning.Core.Pipeline.Contracts.Page.ClassicPageRow;
using ClassicPageDiscovery = PnP.Scanning.Core.Pipeline.Contracts.Page.ClassicPageDiscoveryRow;
using ClassicPageWebPart = PnP.Scanning.Core.Pipeline.Contracts.Page.ClassicPageWebPartRow;
using ClassicWebPartUnique = PnP.Scanning.Core.Pipeline.Contracts.Page.ClassicWebPartUniqueRow;
using ClassicPublishingSiteSummary = PnP.Scanning.Core.Pipeline.Contracts.Site.ClassicPublishingSiteSummaryRow;
using ClassicPageAuditUsage = PnP.Scanning.Core.Pipeline.Contracts.Audit.ClassicPageAuditUsageRow;
using SiteCollection = PnP.Scanning.Core.Pipeline.Contracts.Site.SiteCollectionRow;
using System.Text.Json;
using PnP.Scanning.Core.Pipeline.Analysis.Audit;
using PnP.Scanning.Core.Pipeline.Analysis.Page;
using PnP.Scanning.Core.Pipeline.Analysis.Site;
using PnP.Scanning.Core.Pipeline.Analysis.Web;
using PnP.Scanning.Core.Pipeline.Contracts.Audit;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Pipeline.Contracts.Site;
using PnP.Scanning.Core.Pipeline.Contracts.Web;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Pipeline.Analysis.Page.WebPartMapping;


namespace PnP.Scanning.Core.Pipeline.Analysis.Module;

internal static class ClassicPageReportBuilder
{
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
        void Add<T>(string kind, IEnumerable<T> values, Func<T, string> key)
        {
            var ordinal = 0;
            foreach (var value in values.OrderBy(key, StringComparer.Ordinal))
                rows.Add(new(kind, key(value), ordinal++, VersionedJson.From(value)));
        }
        string PageKey(ClassicPage page) => ClassicPageSourceJson.WebKey(page.SiteUrl, page.WebUrl) + "|" + page.PageUrl;
        var summaries = index.Webs.Values.Where(x => x.State.Succeeded).Select(web => ClassicWebSummaryBuilder.Build(assessmentId, web,
            pages.Where(p => p.SiteUrl == web.SiteUrl && p.WebUrl == web.WebUrl).ToArray(),
            modern.Count(x => x.SiteUrl == web.SiteUrl && x.WebUrl == web.WebUrl))).ToArray();
        Add("classicpages", pages, PageKey);
        Add("classicpagewebparts", parts, p => ClassicPageSourceJson.WebKey(p.SiteUrl, p.WebUrl) + "|" + p.PageUrl + "|" + p.WebPartIndex.ToString("D8"));
        Add("classicwebsummaries", summaries, p => ClassicPageSourceJson.WebKey(p.SiteUrl, p.WebUrl));
        Add("classicsitesummaries", ClassicSiteSummaryBuilder.Build(assessmentId, summaries), p => p.SiteUrl);
        Add("classicwebpartunique", parts.GroupBy(x => x.WebPartType).Select(group => new ClassicWebPartUnique
        {
            ScanId = assessmentId, WebPartType = group.Key, InMappingFile = mapping.InMappingFile(group.Key),
            PageCount = group.Select(x => (x.SiteUrl, x.WebUrl, x.PageUrl)).Distinct().Count(),
        }), p => p.WebPartType);
        var publishing = new List<ClassicPublishingSiteSummary>();
        foreach (var group in summaries.Where(x => x.IsClassicPublishingSite || x.ClassicPublishingPages > 0).GroupBy(x => x.SiteUrl))
        {
            var relevant = pages.Where(x => x.SiteUrl == group.Key && x.PageType == ClassicPageRules.PublishingPage).ToArray();
            var dates = relevant.Where(x => x.ModifiedAt != DateTime.MinValue && x.ModifiedAt != DateTime.MaxValue).Select(x => x.ModifiedAt).ToArray();
            var layouts = relevant.Where(x => !string.IsNullOrEmpty(x.Layout)).Select(x => x.Layout).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            publishing.Add(new() { ScanId = assessmentId, SiteUrl = group.Key, NumberOfWebs = group.Count(),
                NumberOfPages = group.Sum(x => x.ClassicPublishingPages), UsedPageLayouts = layouts.Length > 0 ? string.Join(',', layouts) : null,
                LastPageUpdateDate = dates.Length > 0 ? dates.Max() : null });
        }
        Add("classicpublishingsitesummaries", publishing, x => x.SiteUrl);
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
            Add("classicpageauditusage", AuditRows(assessmentId, index.Scope, index.AuditChunks.Values.ToArray(), audit), x => x.SiteUrl + "|" + x.PageUrl);
        await reports.PublishAsync(rows, token);
    }

    private static List<ClassicPageAuditUsage> AuditRows(Guid assessmentId, ClassicPageScopeSource scope,
        ClassicPageAuditChunkSource[] chunks, List<ClassicAuditPageStats> inputs)
    {
        var succeeded = chunks.Count(x => x.Status == "succeeded");
        var status = succeeded == chunks.Length && chunks.Length > 0 ? "succeeded" : succeeded > 0 ? "partial"
            : chunks.All(x => x.Status == "skipped") ? "skipped" : "failed";
        var reason = string.Join("; ", chunks.Where(x => x.Error != null).OrderBy(x => x.Chunk).Select(x => x.Error));
        var merged = inputs.GroupBy(x => x.PageUrl, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key,
            group => new ClassicPageAuditAnalysis.AuditPageStats(group.Sum(x => x.Views), group.Sum(x => x.Creates),
                group.Sum(x => x.Edits), group.SelectMany(x => x.UserHashes).Distinct().Take(10_000).Count()), StringComparer.OrdinalIgnoreCase);
        var sites = scope.Sites.OrderByDescending(x => x.Length).ToArray();
        var result = new List<ClassicPageAuditUsage>();
        foreach (var site in scope.Sites)
        {
            var stats = merged.Where(x => sites.FirstOrDefault(s => x.Key.StartsWith(s.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)
                || x.Key.Equals(s, StringComparison.OrdinalIgnoreCase)) == site).ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
            string Relative(string absolute) { var origin = new Uri(site).GetLeftPart(UriPartial.Authority); var path = absolute.StartsWith(origin, StringComparison.OrdinalIgnoreCase) ? absolute[origin.Length..] : absolute; return path.Length == 0 ? "/" : path; }
            ClassicPageAuditUsage Row(string url) => new() { ScanId = assessmentId, SiteUrl = site, WebUrl = "/", PageUrl = Relative(url),
                AuditWindowStart = scope.AuditWindowStart, AuditWindowEnd = scope.AuditWindowEnd, QueryStatus = status, SkipReason = string.IsNullOrEmpty(reason) ? null : reason };
            foreach (var value in stats)
            {
                var row = Row(value.Key);
                ClassicPageAuditAnalysis.ApplyAuditUsage(row, stats, value.Key);
                if (status != "partial") row.SkipReason = null;
                result.Add(row);
            }
            if (stats.Count == 0 || status == "partial") result.Add(Row(site));
        }
        return result;
    }
}
