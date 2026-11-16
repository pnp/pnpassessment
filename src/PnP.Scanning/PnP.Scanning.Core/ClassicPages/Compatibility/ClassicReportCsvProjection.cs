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
    internal const string UsageCsv = "classicpageauditusage.csv";
    internal const string OmittedCsv = "classicpagesomitted.csv";

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
                    0,
                    0,
                    null,
                    null),
                token);
            return;
        }

        ClassicPageCsvRow[] rows = SelectPages(source, out ClassicPageOmission[] omitted);
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

        string? usageStatus = source.Publication.Scope.Options.SkipUsageInformation ? null : UsageStatus(source);
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
                source.Publication.Files.LongCount(IsAspx),
                omitted.LongLength,
                usageStatus,
                usageStatus == null ? null : UsageSkipReason(source, usageStatus)),
            token);
        await WriteRowsAsync(directory, OmittedCsv, omitted, configuration, token);
        await ExportRelatedAsync(context, source, rows, directory, configuration, token);
    }

    /// <summary>
    /// Reads how many Pages the export in this folder left out for this assessment. A missing summary, or one written
    /// for another assessment, counts as none.
    /// </summary>
    internal static long ReadOmittedPageCount(string directory, Guid assessmentId)
    {
        string path = Path.Combine(directory, SummaryJson);
        if (!File.Exists(path)) return 0;
        try
        {
            using JsonDocument summary = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = summary.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty(nameof(PageExportSummary.ScanId), out JsonElement scan) &&
                   scan.ValueKind == JsonValueKind.String && scan.TryGetGuid(out Guid id) && id == assessmentId &&
                   root.TryGetProperty(nameof(PageExportSummary.OmittedPageCount), out JsonElement count) &&
                   count.ValueKind == JsonValueKind.Number && count.TryGetInt64(out long value)
                ? value
                : 0;
        }
        catch (Exception error) when (error is IOException or JsonException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Reads the usage state and its reason that the export in this folder recorded for this assessment. A missing
    /// summary, one written for another assessment, and an export without requested usage have neither.
    /// </summary>
    internal static (string? Status, string? Reason) ReadUsageState(string directory, Guid assessmentId)
    {
        string path = Path.Combine(directory, SummaryJson);
        if (!File.Exists(path)) return (null, null);
        try
        {
            using JsonDocument summary = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = summary.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty(nameof(PageExportSummary.ScanId), out JsonElement scan) ||
                scan.ValueKind != JsonValueKind.String || !scan.TryGetGuid(out Guid id) || id != assessmentId)
                return (null, null);
            return (Text(root, nameof(PageExportSummary.UsageQueryStatus)),
                Text(root, nameof(PageExportSummary.UsageSkipReason)));
        }
        catch (Exception error) when (error is IOException or JsonException)
        {
            return (null, null);
        }

        // Reads a text member; an absent member and one that holds no text have no value.
        static string? Text(JsonElement owner, string name) =>
            owner.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    /// <summary>
    /// Words the recorded usage state and its reason for the person reading the report. Complete usage, and usage
    /// that was left out on request, have no message.
    /// </summary>
    internal static string? UsageWarning(string? status, string? reason)
    {
        string? message = status switch
        {
            null or "succeeded" => null,
            "partial" => "Usage data covers only part of the audit window, so the usage counts in the report are " +
                         "incomplete",
            _ => "Usage data could not be read, so the usage columns in the report are empty",
        };
        return message == null || string.IsNullOrEmpty(reason) ? message : message + ". Reason: " + reason;
    }

    /// <summary>
    /// Derives the export-wide usage state from the stored usage queries: <c>skipped</c> when usage was left out on
    /// request or no query could be sent, <c>notcollected</c> without any query, <c>succeeded</c> when every query
    /// succeeded, <c>partial</c> when some did, and <c>failed</c> otherwise.
    /// </summary>
    internal static string UsageStatus(ClassicReportSource source)
    {
        if (source.Publication.Scope.Options.SkipUsageInformation) return "skipped";
        if (source.AuditChunks.Length == 0) return "notcollected";
        int succeeded = source.AuditChunks.Count(chunk => chunk.Status == "succeeded");
        return succeeded == source.AuditChunks.Length
            ? "succeeded"
            : succeeded > 0
                ? "partial"
                : source.AuditChunks.All(chunk => chunk.Status == "skipped")
                    ? "skipped"
                    : "failed";
    }

    /// <summary>
    /// Gives the reason usage is missing or incomplete, from the reasons the usage queries recorded. Reasons of the
    /// same kind (the text before the first colon) are listed once with the number of queries; up to three kinds
    /// are listed and the remaining ones are counted. Each reason is one line of at most 200 characters. Complete
    /// usage has no reason.
    /// </summary>
    internal static string? UsageSkipReason(ClassicReportSource source, string status)
    {
        if (status == "succeeded") return null;
        string[] reasons = source.AuditChunks.Select(chunk => OneLine(chunk.Error))
            .Where(reason => reason.Length > 0)
            .GroupBy(reason => reason.Split(':')[0], StringComparer.Ordinal)
            .OrderBy(kind => kind.Key, StringComparer.Ordinal)
            .Select(kind => kind.Min(StringComparer.Ordinal) +
                            (kind.Count() > 1 ? $" ({kind.Count()} queries)" : ""))
            .ToArray();
        if (reasons.Length == 0) return "No usage query result was recorded for this assessment";
        string listed = string.Join("; ", reasons.Take(3));
        return reasons.Length > 3 ? listed + $"; and {reasons.Length - 3} more" : listed;
    }

    /// <summary>Puts a recorded reason on one line and keeps its first 200 characters.</summary>
    private static string OneLine(string? text)
    {
        string line = string.Join(
            ' ',
            (text ?? "").Split(
                new[] { '\r', '\n', '\t' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return line.Length <= 200 ? line : line[..200] + "...";
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
        {
            string? skipReason = UsageSkipReason(source, UsageStatus(source));
            await WriteRowsAsync(
                directory,
                UsageCsv,
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
                    SkipReason = page.UsageQueryStatus == "succeeded" ? null : skipReason,
                }),
                configuration,
                token);
        }
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
    internal static ClassicPageCsvRow[] SelectPages(ClassicReportSource source) => SelectPages(source, out _);

    /// <summary>
    /// Selects the Pages as above and names every selected Page the URL-keyed CSV cannot carry: a selected file with
    /// no analyzed Page, a Page without an address, and every Page but one of those sharing a site, web and address.
    /// Such a Page is left out of the rows and returned with its reason, so the export continues and stays countable.
    /// </summary>
    internal static ClassicPageCsvRow[] SelectPages(ClassicReportSource source, out ClassicPageOmission[] omitted)
    {
        Dictionary<PageIdentity, ClassicPageRow> pages = source.Publication.Pages.ToDictionary(page =>
            page.Identity?.Validated() ?? throw new SnapshotIntegrityException("CSV Page has no native identity."));
        if (source.Publication.Files.Any(file => file.ScanId != source.AssessmentId) ||
            pages.Values.Any(page => page.ScanId != source.AssessmentId))
            throw new SnapshotIntegrityException("Native report objects belong to a different assessment.");
        List<ClassicPageCsvRow> selected = [];
        List<ClassicPageOmission> left = [];
        foreach (SPFile facts in source.Publication.Files.Where(IsAspx))
        {
            AspxCsvRow file = new(facts);
            string? type = SelectedPageType(file);
            if (type == null) continue;
            PageIdentity identity = PageIdentity.File(facts.SiteCollectionId, facts.FileUniqueId);
            if (!pages.TryGetValue(identity, out ClassicPageRow? page))
            {
                left.Add(ClassicPageOmission.Of(facts, identity, type));
                continue;
            }

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

        Dictionary<(string Site, string Web, string Url), PageIdentity> owners = ClassicCompatibility.AddressOwners(
            selected.Select(row => (row.Identity, row.SiteUrl, row.WebUrl, row.PageUrl)));
        List<ClassicPageCsvRow> kept = [];
        foreach (ClassicPageCsvRow row in selected)
        {
            if (string.IsNullOrWhiteSpace(row.PageUrl))
            {
                left.Add(ClassicPageOmission.Of(row, ClassicPageOmission.NoAddress));
                continue;
            }

            PageIdentity owner = owners[ClassicCompatibility.AddressKey(row.SiteUrl, row.WebUrl, row.PageUrl)];
            if (owner != row.Identity)
            {
                left.Add(ClassicPageOmission.Of(row, ClassicPageOmission.SharedAddress, owner));
                continue;
            }

            kept.Add(row);
        }

        UsageIndex usage = UsageIndex.Create(source);
        foreach (ClassicPageCsvRow row in kept)
        {
            ApplyUsage(row, source, usage);
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

        omitted = left.OrderBy(row => row.ContentKey, StringComparer.Ordinal).ToArray();
        return kept.OrderBy(row => row.ContentKey, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Names the page label the report gives a physical file, or null for a file the report leaves out: one served
    /// as a download, one with another handler than the four selected ones, and a list view or list form.
    /// </summary>
    internal static string? SelectedPageType(AspxCsvRow file)
    {
        string? type = PageTypeForHandler(file.PageHandler);
        return file.NoExecute == true || file.IsListFamily ? null : type;
    }

    /// <summary>
    /// Names the page label the report gives each stored physical file of an assessment, keyed by site collection
    /// and file. Null means the assessment has no native analysis, so its stored page rows are the report.
    /// </summary>
    internal static Dictionary<(Guid SiteCollectionId, Guid FileUniqueId), string>? SelectedFilePageTypes(
        ScanContext context) =>
        context.PhaseRuns.AsNoTracking().Any(run => run.ModuleKey == ClassicPageModule.Key)
            ? SelectedFilePageTypes(context.SPFiles.AsNoTracking())
            : null;

    /// <summary>
    /// Names the page label the report gives each physical file, keyed by site collection and file. Files the
    /// report leaves out have no entry. The files are read one at a time.
    /// </summary>
    internal static Dictionary<(Guid SiteCollectionId, Guid FileUniqueId), string> SelectedFilePageTypes(
        IEnumerable<SPFile> files)
    {
        Dictionary<(Guid SiteCollectionId, Guid FileUniqueId), string> selected = [];
        foreach (SPFile facts in files)
        {
            if (!IsAspx(facts)) continue;
            string? type = SelectedPageType(new AspxCsvRow(facts));
            if (type != null) selected[(facts.SiteCollectionId, facts.FileUniqueId)] = type;
        }

        return selected;
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
    private static void ApplyUsage(ClassicPageCsvRow row, ClassicReportSource source, UsageIndex usage)
    {
        row.AuditWindowStart = source.Publication.Scope.AuditWindowStart;
        row.AuditWindowEnd = source.Publication.Scope.AuditWindowEnd;
        row.UsageQueryStatus = usage.QueryStatus;
        if (usage.Stats == null) return;
        string? locator = row.PageUrl == null
            ? null
            : new Uri(row.SiteUrl).GetLeftPart(UriPartial.Authority) + row.PageUrl;
        if (row.PageUrl?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true) locator = row.PageUrl;
        // The absolute and server-relative locators are tried independently; a statistic is never counted twice.
        IEnumerable<ClassicAuditPageStats> absolute = locator == null
            ? Enumerable.Empty<ClassicAuditPageStats>()
            : usage.Stats[ClassicPageAuditAnalysis.LocatorKey(locator)];
        IEnumerable<ClassicAuditPageStats> relative =
            row.PageUrl?.StartsWith('/') == true && !string.Equals(row.PageUrl, locator, StringComparison.Ordinal)
                ? usage.Stats[ClassicPageAuditAnalysis.LocatorKey(row.PageUrl)]
                : Enumerable.Empty<ClassicAuditPageStats>();
        ClassicAuditPageStats[] stats = absolute.Concat(relative).ToArray();
        if (row.ObjectKind == nameof(PageObjectKind.ListItem) && stats.Length == 0)
        {
            row.UsageAttributionState = "ItemLocatorNotObserved";
            return;
        }

        row.UsageAttributionState = stats.Length == 0 ? "CoveredFileNoEvents" : "ExactContentLocator";
        row.AuditViewsCount = stats.Sum(stat => stat.Views);
        row.AuditCreatesCount = stats.Sum(stat => stat.Creates);
        row.AuditEditsCount = stats.Sum(stat => stat.Edits);
        row.AuditUniqueUsers = stats.Length == 1
            ? stats[0].UserHashes.Distinct(StringComparer.Ordinal).Count()
            : stats.SelectMany(stat => stat.UserHashes).Distinct(StringComparer.Ordinal).Count();
    }

    /// <summary>
    /// Holds the export-wide usage state and the audit statistics grouped once by their full-locator comparison key,
    /// so each Page is a keyed lookup instead of a scan of every audit row.
    /// </summary>
    private sealed record UsageIndex(string QueryStatus, ILookup<string, ClassicAuditPageStats>? Stats)
    {
        /// <summary>Derives the shared query status once and indexes statistics only when a succeeded chunk exists.</summary>
        internal static UsageIndex Create(ClassicReportSource source)
        {
            string status = UsageStatus(source);
            return new(
                status,
                status is "succeeded" or "partial"
                    ? source.Publication.Audit.Where(stat => stat.PageUrl != null)
                        .ToLookup(
                            stat => ClassicPageAuditAnalysis.LocatorKey(stat.PageUrl),
                            StringComparer.Ordinal)
                    : null);
        }
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
        long PhysicalAspxCount,
        long OmittedPageCount,
        string? UsageQueryStatus,
        string? UsageSkipReason);

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