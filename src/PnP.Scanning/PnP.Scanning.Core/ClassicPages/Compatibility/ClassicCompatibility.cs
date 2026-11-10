using PnP.Scanning.Core.ClassicPages.Contracts.Module;
using PnP.Scanning.Core.ClassicPages.Analysis.Page;
#nullable enable
using PnP.Scanning.Core.Storage;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.ClassicPages.Analysis.Audit;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.ClassicPages.Contracts.Web;
using PnP.Scanning.Core.ClassicPages.Contracts.Page;
using System.Text.Json.Nodes;
using PnP.Scanning.Core.Scanners;

namespace PnP.Scanning.Core.ClassicPages.Compatibility;

// This adapter retains the existing database/report contract. It does not define a new CSV shape.
/// <summary>
/// Binds deterministic analysis output to the existing Classic storage and report contracts.
/// </summary>
internal sealed record ClassicCompatibilityPayload(
    ClassicPageRow[] Pages,
    ClassicPageWebPart[] Parts,
    ClassicPageDiscovery[] Discovery,
    SPFile[] Files,
    WebFacts[] Webs,
    ClassicCollectionScope Scope,
    ClassicComponentSource[] Components,
    ClassicAuditPageStats[] Audit);

/// <summary>
/// Builds the narrow existing-report projection from fixed source and committed results.
/// </summary>
internal static class ClassicCompatibility
{
    internal static readonly Scanners.WebPartMapping.WebPartMappingManager MappingManager = new();

    /// <summary>
    /// Adapts finalized native results to the unchanged immutable legacy report payload.
    /// </summary>
    internal static Task PublishAsync(
        ClassicPagePublication output,
        IAnalysisReportWriter writer,
        CancellationToken token)
    {
        ClassicCompatibilityPayload payload = new(
            output.Pages,
            output.Parts.Select(CollectionJson.Convert<ClassicPageWebPart>).ToArray(),
            output.Discovery.Select(DiscoveryEvidenceMapper.ToLegacy).ToArray(),
            output.Files.Select(CollectionJson.Convert<SPFile>).ToArray(),
            output.Webs,
            output.Scope,
            output.Components,
            output.Audit);
        return writer.PublishAsync([new("ClassicCompatibility", "assessment", 0, VersionedJson.From(payload))], token);
    }

    /// <summary>
    /// Adapts optional backing-list metadata to the historical storage shape only; the required legacy sentinel never becomes native Page identity.
    /// </summary>
    internal static ClassicPage ToLegacyPage(ClassicPageRow page)
    {
        JsonObject values = JsonSerializer.SerializeToNode(page)?.AsObject() ??
                            throw new SnapshotIntegrityException(
                                "Page compatibility serialization returned no object.");
        values[nameof(ClassicPage.ListId)] = JsonSerializer.SerializeToNode(page.ListId ?? Guid.Empty);
        return values.Deserialize<ClassicPage>() ??
               throw new SnapshotIntegrityException("Page compatibility serialization failed.");
    }

    /// <summary>
    /// Rejects unrepresentable or colliding legacy URL keys before writes; canonical native objects remain in immutable analysis publication.
    /// </summary>
    internal static ClassicPage[] LegacyPages(ClassicPageRow[] pages)
    {
        Dictionary<(string Site, string Web, string Url), PageIdentity> legacyKeys = [];
        List<ClassicPage> legacy = [];
        foreach (ClassicPageRow page in pages.Where(page => page.PageType != ClassicPageRules.ModernPage))
        {
            PageIdentity identity = page.Identity?.Validated() ??
                                    throw new SnapshotIntegrityException(
                                        "Compatibility output requires native Page identity.");
            if (string.IsNullOrWhiteSpace(page.PageUrl))
                throw new SnapshotIntegrityException(
                    $"The existing report contract cannot represent content {identity.Key} without an acquired locator; native evidence is retained.");
            (string Site, string Web, string Url) key = (page.SiteUrl.ToUpperInvariant(),
                page.WebUrl.ToUpperInvariant(), page.PageUrl.ToUpperInvariant());
            if (legacyKeys.TryGetValue(key, out PageIdentity? previous) && previous != identity)
                throw new SnapshotIntegrityException(
                    $"Distinct native content {previous.Key} and {identity.Key} share one legacy URL key. The existing report contract cannot represent both; no object was discarded.");
            legacyKeys.Add(key, identity);
            legacy.Add(ToLegacyPage(page));
        }

        return legacy.ToArray();
    }

    /// <summary>
    /// Builds existing Web totals while retaining captured non-page component results.
    /// </summary>
    internal static ClassicWebSummary BuildWeb(
        Guid assessment,
        WebFacts source,
        ClassicPage[] pages,
        ClassicPageDiscovery[] discovery,
        ClassicWebSummary? other)
    {
        ClassicWebSummary? row = other ?? new ClassicWebSummary();
        row.ScanId = assessment;
        row.SiteUrl = source.SiteUrl;
        row.WebUrl = source.WebUrl;
        row.Template = source.WebTemplate;
        row.LastItemUserModifiedDate = source.LastItemUserModifiedDate;
        ClassicPage[]? selected = pages.Where(x => x.SiteUrl == source.SiteUrl && x.WebUrl == source.WebUrl).ToArray();
        row.ClassicPages = selected.Length;
        row.ClassicWikiPages = selected.Count(x => x.PageType == ClassicPageRules.WikiPage);
        row.ClassicASPXPages = selected.Count(x => x.PageType == ClassicPageRules.ASPXPage);
        row.ClassicBlogPages =
            selected.Count(x => x.PageType is ClassicPageRules.BlogPage or ClassicPageRules.DelveBlogPage);
        row.ClassicWebPartPages = selected.Count(x => x.PageType == ClassicPageRules.WebPartPage);
        row.ClassicPublishingPages = selected.Count(x => x.PageType == ClassicPageRules.PublishingPage);
        row.ModernPages = discovery.Count(x => x.SiteUrl == source.SiteUrl && x.WebUrl == source.WebUrl &&
                                               x.RowType == "Page" && x.PageType == ClassicPageRules.ModernPage);
        SiteType type = ClassicScanner.GetSiteType(source.WebTemplate);
        row.IsClassicPublishingSite = type == SiteType.Publishing;
        row.IsModernSite = type == SiteType.Modern;
        row.IsModernCommunicationSite = type == SiteType.Communication;
        row.RemediationCode = type == SiteType.Publishing
            ? "CS2"
            : type == SiteType.Blog
                ? "CS1"
                : null;
        row.AggregatedRemediationCodes = string.Join(
            ',',
            selected.Select(x => x.RemediationCode)
                .Append(row.RemediationCode)
                .Concat((row.AggregatedRemediationCodes ?? "").Split(','))
                .Where(x => !string.IsNullOrEmpty(x))
                .Distinct()
                .OrderBy(x => x, StringComparer.Ordinal));
        return row;
    }

    /// <summary>
    /// Aggregates existing Web summaries into the established per-site counts and weighted readiness totals.
    /// </summary>
    internal static ClassicSiteSummary BuildSite(Guid assessment, IGrouping<string, ClassicWebSummary> group)
    {
        ClassicWebSummary? root = group.FirstOrDefault(x => x.WebUrl == "/");
        ClassicWebSummary[]? sub = group.Where(x => x.WebUrl != "/").ToArray();
        int weighted = group.Sum(x => x.PagesWithWebParts);
        return new()
        {
            ScanId = assessment,
            SiteUrl = group.Key,
            RootWebTemplate = root?.Template,
            SubWebTemplates = string.Join(',', sub.Select(x => x.Template).Distinct()),
            SubWebCount = sub.Length,
            SubWebDepth = sub.Length == 0 ? 0 : sub.Max(x => x.WebUrl.Count(c => c == '/')),
            LastItemUserModifiedDate = group.Max(x => x.LastItemUserModifiedDate),
            ClassicLists = group.Sum(x => x.ClassicLists),
            ModernLists = group.Sum(x => x.ModernLists),
            ClassicPages = group.Sum(x => x.ClassicPages),
            ModernPages = group.Sum(x => x.ModernPages),
            ClassicWikiPages = group.Sum(x => x.ClassicWikiPages),
            ClassicASPXPages = group.Sum(x => x.ClassicASPXPages),
            ClassicBlogPages = group.Sum(x => x.ClassicBlogPages),
            ClassicWebPartPages = group.Sum(x => x.ClassicWebPartPages),
            ClassicPublishingPages = group.Sum(x => x.ClassicPublishingPages),
            PagesWithWebParts = weighted,
            MappableWebPartPages = group.Sum(x => x.MappableWebPartPages),
            UnmappedWebPartPages = group.Sum(x => x.UnmappedWebPartPages),
            UncustomizedHomePages = group.Sum(x => x.UncustomizedHomePages),
            AvgMappingPercentage =
                weighted == 0 ? 0 : group.Sum(x => x.AvgMappingPercentage * x.PagesWithWebParts) / weighted,
            ClassicWorkflows = group.Sum(x => x.ClassicWorkflows),
            ClassicInfoPathForms = group.Sum(x => x.ClassicInfoPathForms),
            ClassicExtensibilities = group.Sum(x => x.ClassicExtensibilities),
            SharePointAddIns = group.Sum(x => x.SharePointAddIns),
            AzureACSPrincipals = group.Sum(x => x.AzureACSPrincipals),
            AggregatedRemediationCodes = string.Join(
                ',',
                group.SelectMany(x => (x.AggregatedRemediationCodes ?? "").Split(','))
                    .Where(x => !string.IsNullOrEmpty(x))
                    .Distinct()
                    .OrderBy(x => x, StringComparer.Ordinal)),
        };
    }
}