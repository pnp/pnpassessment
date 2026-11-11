#nullable enable
using System.Text;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.ClassicPages.Analysis.Audit;
using PnP.Scanning.Core.ClassicPages.Contracts.Page;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Storage.Pipeline;

namespace PnP.Scanning.Core.ClassicPages.Compatibility;

/// <summary>
/// Keeps unfiltered physical ASPX inventory separate from the user's exact native Page export predicate.
/// </summary>
internal static class ClassicReportCsvProjection
{
    internal const string AspxCsv = "aspx.csv";
    internal const string PagesCsv = "classicpages.csv";
    internal const string SummaryJson = "classicpagessummary.json";

    /// <summary>
    /// Writes all physical ASPX SQL facts and then exports Pages from the latest verified native publication.
    /// Historical assessments without a pipeline retain their existing labelled legacy CSV, never a fabricated native result.
    /// </summary>
    internal static async Task ExportAsync(
        ScanContext context,
        Guid assessmentId,
        string directory,
        CsvConfiguration configuration,
        CancellationToken token = default)
    {
        await ExportAspxAsync(context, assessmentId, directory, configuration, token);
        ClassicReportSource? source = await PipelineStore.ReadClassicReportSourceAsync(context, assessmentId, token);
        if (source == null)
        {
            long count = 0;
            await using StreamWriter legacyWriter = new(
                Path.Combine(directory, PagesCsv),
                false,
                new UTF8Encoding(false));
            using CsvWriter legacyCsv = new(legacyWriter, configuration);
            legacyCsv.WriteHeader<ClassicPage>();
            await legacyCsv.NextRecordAsync();
            await foreach (ClassicPage page in context.ClassicPages.AsNoTracking()
                               .Where(page => page.ScanId == assessmentId)
                               .OrderBy(page => page.SiteUrl)
                               .ThenBy(page => page.WebUrl)
                               .ThenBy(page => page.PageUrl)
                               .AsAsyncEnumerable()
                               .WithCancellation(token))
            {
                legacyCsv.WriteRecord(page);
                await legacyCsv.NextRecordAsync();
                count++;
            }

            await WriteSummaryAsync(
                directory,
                new PageExportSummary(
                    "classic-pages-csv/v1",
                    assessmentId,
                    "LegacyContract;NativeSourceUnavailable",
                    null,
                    null,
                    count,
                    0,
                    0,
                    0,
                    0,
                    0,
                    count,
                    0),
                token);
            return;
        }

        ClassicPageCsvRow[] rows = SelectPages(source);
        await using StreamWriter writer = new(Path.Combine(directory, PagesCsv), false, new UTF8Encoding(false));
        using CsvWriter csv = new(writer, configuration);
        csv.WriteHeader<ClassicPageCsvRow>();
        await csv.NextRecordAsync();
        foreach (ClassicPageCsvRow row in rows)
        {
            token.ThrowIfCancellationRequested();
            csv.WriteRecord(row);
            await csv.NextRecordAsync();
        }

        await WriteSummaryAsync(
            directory,
            new PageExportSummary(
                "classic-pages-csv/v1",
                assessmentId,
                "NativePublication;NoExecuteIsNotTrueAndExactHandlerAndNotListFamilyUnionPosts301",
                source.AnalysisRunId,
                source.SnapshotId,
                rows.LongLength,
                rows.LongCount(row => row.ObjectKind == nameof(PageObjectKind.File)),
                rows.LongCount(row => row.ObjectKind == nameof(PageObjectKind.ListItem)),
                rows.LongCount(row => row.NoExecute == true),
                rows.LongCount(row => row.NoExecute == false),
                rows.LongCount(row => row.ObjectKind == nameof(PageObjectKind.File) && row.NoExecute == null),
                0,
                source.Publication.Files.LongCount(IsAspx)),
            token);
        await ExportRelatedAsync(context, source, rows, directory, configuration, token);
    }

    /// <summary>
    /// Rebuilds exported page-dependent rollups and part/usage inventories from the same selected Pages without writing database tables.
    /// Retained non-page component counts and publishing master-page metadata are unchanged.
    /// </summary>
    private static async Task ExportRelatedAsync(
        ScanContext context,
        ClassicReportSource source,
        ClassicPageCsvRow[] pages,
        string directory,
        CsvConfiguration configuration,
        CancellationToken token)
    {
        Guid assessment = source.AssessmentId;
        HashSet<(string Site, string Web, string Url)> selected = pages
            .Select(page => (page.SiteUrl, page.WebUrl, page.PageUrl!))
            .ToHashSet();
        ClassicPageWebPart[] parts = source.Publication.Parts
            .Where(part => selected.Contains((part.SiteUrl, part.WebUrl, part.PageUrl)))
            .ToArray();
        List<ClassicWebSummary> webs = await context.ClassicWebSummaries.AsNoTracking()
            .Where(web => web.ScanId == assessment)
            .ToListAsync(token);
        foreach (ClassicWebSummary web in webs)
        {
            ClassicPageCsvRow[] owned = pages.Where(page => page.SiteUrl == web.SiteUrl && page.WebUrl == web.WebUrl)
                .ToArray();
            web.ClassicPages = owned.Length;
            web.ClassicWikiPages = owned.Count(page => page.PageType == "WikiPage");
            web.ClassicASPXPages = owned.Count(page => page.PageType == "ASPXPage");
            web.ClassicBlogPages = owned.Count(page => page.PageType == "BlogPage");
            web.ClassicWebPartPages = owned.Count(page => page.PageType == "WebPartPage");
            web.ClassicPublishingPages = owned.Count(page => page.PageType == "PublishingPage");
            web.ModernPages = 0;
            ClassicPageCsvRow[] measured = owned.Where(page => page.WebPartCount > 0 && page.MappingPercentage.HasValue)
                .ToArray();
            web.PagesWithWebParts = measured.Length;
            web.MappableWebPartPages = measured.Count(page => page.MappingPercentage == 100);
            web.UnmappedWebPartPages = measured.Length - web.MappableWebPartPages;
            web.AvgMappingPercentage = measured.Length == 0
                ? 0
                : measured.Average(page =>
                    page.MappingPercentage ??
                    throw new SnapshotIntegrityException("A measured readiness row has no mapping percentage."));
            web.UncustomizedHomePages = owned.Count(page => page.UncustomizedHomePage == true);
            web.AggregatedRemediationCodes = string.Join(
                ',',
                (web.AggregatedRemediationCodes ?? "").Split(',')
                .Where(code => !string.IsNullOrEmpty(code) && code is not ("CP1" or "CP2" or "CP3" or "CP4" or "CP5"))
                .Concat(owned.Select(page => page.RemediationCode).Where(code => !string.IsNullOrEmpty(code)))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(code => code, StringComparer.Ordinal));
        }

        await WriteRowsAsync(directory, "classicpagewebparts.csv", parts, configuration, token);
        Dictionary<string, bool> mappings = await context.ClassicWebPartUniques.AsNoTracking()
            .Where(part => part.ScanId == assessment)
            .ToDictionaryAsync(part => part.WebPartType, part => part.InMappingFile, token);
        await WriteRowsAsync(
            directory,
            "classicwebpartunique.csv",
            parts.GroupBy(part => part.WebPartType)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new ClassicWebPartUnique
                {
                    ScanId = assessment,
                    WebPartType = group.Key,
                    InMappingFile =
                        mappings.TryGetValue(group.Key, out bool mapped)
                            ? mapped
                            : throw new SnapshotIntegrityException(
                                "Selected web-part type has no committed mapping summary."),
                    PageCount = group.Select(part => (part.SiteUrl, part.WebUrl, part.PageUrl)).Distinct().Count(),
                }),
            configuration,
            token);
        await WriteRowsAsync(directory, "classicwebsummaries.csv", webs, configuration, token);
        await WriteRowsAsync(
            directory,
            "classicsitesummaries.csv",
            webs.GroupBy(web => web.SiteUrl).Select(group => ClassicCompatibility.BuildSite(assessment, group)),
            configuration,
            token);
        List<ClassicPublishingSiteSummary> publishing = await context.ClassicPublishingSiteSummaries.AsNoTracking()
            .Where(site => site.ScanId == assessment)
            .ToListAsync(token);
        foreach (string siteUrl in pages.Where(page => page.PageType == "PublishingPage")
                     .Select(page => page.SiteUrl)
                     .Distinct(StringComparer.Ordinal))
            if (publishing.All(site => site.SiteUrl != siteUrl))
                publishing.Add(new ClassicPublishingSiteSummary { ScanId = assessment, SiteUrl = siteUrl });
        foreach (ClassicPublishingSiteSummary site in publishing)
        {
            ClassicPageCsvRow[] owned = pages
                .Where(page => page.SiteUrl == site.SiteUrl && page.PageType == "PublishingPage")
                .ToArray();
            site.NumberOfPages = owned.Length;
            site.NumberOfWebs = webs.Count(web =>
                web.SiteUrl == site.SiteUrl && (web.IsClassicPublishingSite || web.ClassicPublishingPages > 0));
            site.UsedPageLayouts = string.Join(
                ',',
                owned.Select(page => page.Layout)
                    .Where(layout => !string.IsNullOrEmpty(layout))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(layout => layout, StringComparer.Ordinal));
            site.LastPageUpdateDate = owned.Length == 0 ? null : owned.Max(page => page.ModifiedAt);
        }

        await WriteRowsAsync(directory, "classicpublishingsitesummaries.csv", publishing, configuration, token);
        if (!source.Publication.Scope.Options.SkipUsageInformation)
            await WriteRowsAsync(
                directory,
                "classicpageauditusage.csv",
                pages.Select(page => new
                {
                    page.ScanId,
                    page.SiteUrl,
                    page.PageUrl,
                    page.AuditViewsCount,
                    page.AuditCreatesCount,
                    page.AuditEditsCount,
                    page.AuditUniqueUsers,
                    page.AuditWindowStart,
                    page.AuditWindowEnd,
                    QueryStatus = page.UsageQueryStatus,
                    page.UsageAttributionState,
                    SkipReason =
                        page.UsageQueryStatus == "succeeded" ? null : "See native audit coverage and UsageQueryStatus.",
                }),
                configuration,
                token);
    }

    /// <summary>Writes a fixed related CSV schema from its selected native projection, including the header for empty results.</summary>
    private static async Task WriteRowsAsync<T>(
        string directory,
        string file,
        IEnumerable<T> rows,
        CsvConfiguration configuration,
        CancellationToken token)
    {
        await using StreamWriter writer = new(Path.Combine(directory, file), false, new UTF8Encoding(false));
        using CsvWriter csv = new(writer, configuration);
        csv.WriteHeader<T>();
        await csv.NextRecordAsync();
        foreach (T row in rows)
        {
            token.ThrowIfCancellationRequested();
            csv.WriteRecord(row);
            await csv.NextRecordAsync();
        }
    }

    /// <summary>
    /// Streams native SQL file rows once; the only admission condition is the physical ASPX extension.
    /// Modern, catalog, registered forms/views, poisoned, denied and unknown files are all retained.
    /// </summary>
    internal static async Task ExportAspxAsync(
        ScanContext context,
        Guid assessmentId,
        string directory,
        CsvConfiguration configuration,
        CancellationToken token = default)
    {
        await using StreamWriter writer = new(Path.Combine(directory, AspxCsv), false, new UTF8Encoding(false));
        using CsvWriter csv = new(writer, configuration);
        csv.Context.TypeConverterCache.AddConverter<string>(new ClassicAspxNullableStringConverter());
        csv.WriteHeader<AspxCsvRow>();
        await csv.NextRecordAsync();
        long count = 0, blocked = 0, allowed = 0, unknown = 0;
        await foreach (SPFile file in context.SPFiles.AsNoTracking()
                           .Where(file => file.ScanId == assessmentId)
                           .OrderBy(file => file.SiteCollectionId)
                           .ThenBy(file => file.FileUniqueId)
                           .AsAsyncEnumerable()
                           .WithCancellation(token))
        {
            if (!IsAspx(file.FileName, file.ServerRelativeUrl)) continue;
            AspxCsvRow row = new(file);
            csv.WriteRecord(row);
            await csv.NextRecordAsync();
            count++;
            if (row.NoExecute == true)
                blocked++;
            else if (row.NoExecute == false)
                allowed++;
            else
                unknown++;
        }

        await File.WriteAllTextAsync(
            Path.Combine(directory, "aspxsummary.json"),
            JsonSerializer.Serialize(
                new AspxExportSummary(
                    "physical-aspx-csv/v1",
                    assessmentId,
                    count,
                    blocked,
                    allowed,
                    unknown,
                    "PhysicalAspxExtensionOnly;NoBusinessFilters",
                    ClassicAspxNullableStringConverter.EncodingVersion),
                new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false),
            token);
    }

    /// <summary>
    /// Selects file Pages whose NoExecute is not true, whose source handler is one of the exact four, and which are
    /// outside the List product family (a registered list view or form); then unions actual Posts items.
    /// Unknown NoExecute and unknown list-role evidence are selected. Catalog role, experience, official status and
    /// item availability stay outside the predicate, and distinct native objects are never merged by URL.
    /// </summary>
    internal static ClassicPageCsvRow[] SelectPages(ClassicReportSource source)
    {
        Dictionary<PageIdentity, ClassicPageRow> pages = source.Publication.Pages.ToDictionary(page =>
            page.Identity?.Validated() ?? throw new SnapshotIntegrityException("CSV Page has no native identity."));
        if (source.Publication.Files.Any(file => file.ScanId != source.AssessmentId) ||
            pages.Values.Any(page => page.ScanId != source.AssessmentId))
            throw new SnapshotIntegrityException("Native report objects belong to a different assessment.");
        List<ClassicPageCsvRow> selected = [];
        foreach (SPFile facts in source.Publication.Files.Where(IsAspx))
        {
            AspxCsvRow file = new(facts);
            string? type = PageTypeForHandler(file.PageHandler);
            if (file.NoExecute == true || type == null || file.IsListFamily) continue;
            PageIdentity identity = PageIdentity.File(facts.SiteCollectionId, facts.FileUniqueId);
            if (!pages.TryGetValue(identity, out ClassicPageRow? page))
                throw new SnapshotIntegrityException(
                    "An admitted ASPX file has no canonical Page; incomplete publication cannot substitute for export.");
            selected.Add(
                new(
                    page,
                    file,
                    type,
                    source.AnalysisRunId,
                    source.SnapshotId,
                    source.Measurements.GetValueOrDefault(identity)));
        }

        foreach (ClassicPageRow page in
                 pages.Values.Where(page => page.Identity!.ObjectKind == PageObjectKind.ListItem))
        {
            if (page.ListTemplate != 301)
                throw new SnapshotIntegrityException("Independent Page export requires an observed Posts template.");
            selected.Add(new(page, null, "BlogPage", source.AnalysisRunId, source.SnapshotId));
        }

        Dictionary<(string Site, string Web, string Url), PageIdentity> locators = [];
        foreach (ClassicPageCsvRow row in selected)
        {
            if (string.IsNullOrWhiteSpace(row.PageUrl))
                throw new SnapshotIntegrityException("Selected native Page has no acquired report locator.");
            (string Site, string Web, string Url) key = (row.SiteUrl.ToUpperInvariant(), row.WebUrl.ToUpperInvariant(),
                ClassicPageAuditAnalysis.LocatorKey(row.PageUrl));
            PageIdentity identity = row.Identity;
            if (locators.TryGetValue(key, out PageIdentity? previous) && previous != identity)
                throw new SnapshotIntegrityException(
                    "Distinct native Pages share one legacy URL key; CSV export cannot silently deduplicate them.");
            locators.Add(key, identity);
        }

        foreach (ClassicPageCsvRow row in selected)
        {
            ApplyUsage(row, source);
            Contracts.Web.WebFacts? web = source.Publication.Webs.SingleOrDefault(web =>
                web.SiteCollectionId == row.SiteCollectionId && web.WebId == row.WebId);
            row.CanModernizeReadState = web?.CanModernizeState.Status ?? "NotAcquired";
            row.WebHomeFallbackReadState = web?.HomeFallbackState.Status ?? "NotAcquired";
            if (row.HomePage == true && web?.CanModernizeState.Succeeded == true)
                row.UncustomizedHomePage = web.CanModernizeHomepage;
            else if (row.HomePage == true &&
                     source.Measurements.GetValueOrDefault(row.Identity)?.HomeFallbackKnown == true)
                row.UncustomizedHomePage = pages[row.Identity].UncustomizedHomePage;
        }

        return selected.OrderBy(row => row.ContentKey, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Maps exact fully qualified handler names to familiar page labels without guessing aliases or metadata types.</summary>
    internal static string? PageTypeForHandler(string? handler) =>
        handler switch
        {
            "Microsoft.SharePoint.WebPartPages.WikiEditPage" => "WikiPage",
            "Microsoft.SharePoint.WebPartPages.WebPartPage" => "WebPartPage",
            "Microsoft.SharePoint.Publishing.TemplateRedirectionPage" => "PublishingPage",
            "System.Web.UI.Page" => "ASPXPage",
            _ => null,
        };

    /// <summary>Recognizes extension membership only, not a business classification or source-read success state.</summary>
    internal static bool IsAspx(string? name, string? path) =>
        name?.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase) == true ||
        path?.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Tests the original physical-file facts without assigning a final page family.</summary>
    private static bool IsAspx(SPFile file) => IsAspx(file.FileName, file.ServerRelativeUrl);

    /// <summary>
    /// Joins usage to the exact content locator including the Posts item query, never to its shared renderer.
    /// Failed, skipped and uncollected queries retain null counts; partial counts remain explicitly labelled partial.
    /// </summary>
    private static void ApplyUsage(ClassicPageCsvRow row, ClassicReportSource source)
    {
        row.AuditWindowStart = source.Publication.Scope.AuditWindowStart;
        row.AuditWindowEnd = source.Publication.Scope.AuditWindowEnd;
        if (source.Publication.Scope.Options.SkipUsageInformation)
        {
            row.UsageQueryStatus = "skipped";
            return;
        }

        if (source.AuditChunks.Length == 0)
        {
            row.UsageQueryStatus = "notcollected";
            return;
        }

        int succeeded = source.AuditChunks.Count(chunk => chunk.Status == "succeeded");
        row.UsageQueryStatus = succeeded == source.AuditChunks.Length
            ? "succeeded"
            : succeeded > 0
                ? "partial"
                : source.AuditChunks.All(chunk => chunk.Status == "skipped")
                    ? "skipped"
                    : "failed";
        if (succeeded == 0) return;
        string? locator = row.PageUrl == null
            ? null
            : new Uri(row.SiteUrl).GetLeftPart(UriPartial.Authority) + row.PageUrl;
        if (row.PageUrl?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true) locator = row.PageUrl;
        ClassicAuditPageStats[] stats = source.Publication.Audit.Where(stat =>
                ClassicPageAuditAnalysis.LocatorEquals(stat.PageUrl, locator) || row.PageUrl?.StartsWith('/') == true &&
                ClassicPageAuditAnalysis.LocatorEquals(stat.PageUrl, row.PageUrl))
            .ToArray();
        if (row.ObjectKind == nameof(PageObjectKind.ListItem) && stats.Length == 0)
        {
            row.UsageAttributionState = "ItemLocatorNotObserved";
            return;
        }

        row.UsageAttributionState = stats.Length == 0 ? "CoveredFileNoEvents" : "ExactContentLocator";
        row.AuditViewsCount = stats.Sum(stat => stat.Views);
        row.AuditCreatesCount = stats.Sum(stat => stat.Creates);
        row.AuditEditsCount = stats.Sum(stat => stat.Edits);
        row.AuditUniqueUsers = stats.SelectMany(stat => stat.UserHashes).Distinct(StringComparer.Ordinal).Count();
    }

    /// <summary>Writes counts from the exact selected/written CSV set rather than legacy-table or raw-inventory denominators.</summary>
    private static Task WriteSummaryAsync(string directory, PageExportSummary summary, CancellationToken token) =>
        File.WriteAllTextAsync(
            Path.Combine(directory, SummaryJson),
            JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false),
            token);

    /// <summary>Separates the Pages numerator, raw ASPX denominator, unknown values and historical compatibility state.</summary>
    private sealed record PageExportSummary(
        string SchemaVersion,
        Guid ScanId,
        string SelectionState,
        Guid? AnalysisRunId,
        Guid? SnapshotId,
        long ExportedPageCount,
        long AspxPageCount,
        long PostsPageCount,
        long NoExecuteTrueCount,
        long NoExecuteFalseCount,
        long NoExecuteUnknownCount,
        long LegacyPageCount,
        long PhysicalAspxCount);

    /// <summary>Counts every raw SQL ASPX row actually written, including blocked, allowed and unknown NoExecute observations.</summary>
    private sealed record AspxExportSummary(
        string SchemaVersion,
        Guid ScanId,
        long ExportedAspxCount,
        long NoExecuteTrueCount,
        long NoExecuteFalseCount,
        long NoExecuteUnknownCount,
        string SelectionState,
        string NullableStringEncoding);
}