#nullable enable
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.ClassicPages.Analysis.Page;
using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.ClassicPages.Compatibility;

/// <summary>Joins retained legacy usage without changing page identities, query coverage or database rows.</summary>
internal sealed class LegacyClassicPageAuditProjection
{
    private readonly bool skipUsage;
    private readonly Dictionary<(string SiteUrl, string PageUrl), ClassicPageAuditUsage> auditByPage = new();
    private readonly Dictionary<string, ClassicPageAuditUsage?> coverageBySite = new(StringComparer.Ordinal);
    private readonly HashSet<(string SiteUrl, string PageUrl)> ambiguousPages = new();

    private LegacyClassicPageAuditProjection(bool skipUsage) => this.skipUsage = skipUsage;

    internal static async Task<LegacyClassicPageAuditProjection> LoadAsync(
        ScanContext context,
        Guid assessmentId,
        CancellationToken token)
    {
        List<string> settings = await context.Properties.AsNoTracking()
            .Where(setting => setting.ScanId == assessmentId && setting.Name.ToLower() == "skipusageinformation")
            .Select(setting => setting.Value)
            .ToListAsync(token);
        var projection = new LegacyClassicPageAuditProjection(
            settings.Any(value => bool.TryParse(value, out bool skip) && skip));
        if (projection.skipUsage) return projection;

        List<ClassicPageAuditUsage> usage = await context.ClassicPageAuditUsages.AsNoTracking()
            .Where(row => row.ScanId == assessmentId)
            .ToListAsync(token);
        // The audit table's primary key already guarantees one record per assessment/site/locator.
        foreach (ClassicPageAuditUsage row in usage)
        {
            if (!HasLocator(row)) continue;
            if (!IsSiteSummary(row))
            {
                projection.auditByPage.Add((row.SiteUrl, row.PageUrl), row);
                continue;
            }

            // Multiple summary locators for one site cannot establish unambiguous coverage.
            if (!projection.coverageBySite.TryAdd(row.SiteUrl, row))
                projection.coverageBySite[row.SiteUrl] = null;
        }

        if (usage.Count == 0) return projection;
        var duplicateKeys = await context.ClassicPages.AsNoTracking()
            .Where(page => page.ScanId == assessmentId)
            .GroupBy(page => new { page.SiteUrl, page.PageUrl })
            .Where(group => group.Count() > 1)
            .Select(group => new { group.Key.SiteUrl, group.Key.PageUrl })
            .ToListAsync(token);
        foreach (var key in duplicateKeys) projection.ambiguousPages.Add((key.SiteUrl, key.PageUrl));
        return projection;
    }

    internal LegacyClassicPageCsvRow Project(ClassicPage page)
    {
        var row = new LegacyClassicPageCsvRow(page);
        if (skipUsage)
        {
            row.UsageQueryStatus = "skipped";
            return row;
        }

        if (string.IsNullOrEmpty(page.SiteUrl) || string.IsNullOrEmpty(page.PageUrl)) return row;
        var key = (page.SiteUrl, page.PageUrl);
        if (ambiguousPages.Contains(key)) return row;
        auditByPage.TryGetValue(key, out ClassicPageAuditUsage? exact);
        coverageBySite.TryGetValue(page.SiteUrl, out ClassicPageAuditUsage? coverage);
        ClassicPageAuditUsage? measurement = exact ?? coverage;
        if (measurement != null) SetCoverage(row, measurement);

        // Older collectors could retain only the shared Posts renderer. It cannot describe an individual item.
        bool blog = page.PageType == ClassicPageRules.BlogPage;
        if (blog && (!HasItemLocator(page) || exact == null))
        {
            row.UsageAttributionState = "ItemLocatorNotObserved";
            return row;
        }

        if (exact != null)
        {
            if (row.UsageQueryStatus is "succeeded" or "partial")
            {
                SetCounts(row, exact);
                row.UsageAttributionState = "ExactContentLocator";
            }
            return row;
        }

        // A missing row is not a measured zero. Only the legacy collector's explicit, fully queried
        // empty-site sentinel proves zero, and it cannot prove zero for query-addressed objects.
        if (coverage != null && row.UsageQueryStatus == "succeeded" &&
            coverage.AuditWindowStart > DateTime.MinValue &&
            coverage.AuditWindowEnd > coverage.AuditWindowStart &&
            coverage.AuditWindowEnd < DateTime.MaxValue &&
            coverage.AuditViewsCount == 0 && coverage.AuditCreatesCount == 0 &&
            coverage.AuditEditsCount == 0 && coverage.AuditUniqueUsers == 0 &&
            !page.PageUrl.Contains('?') && !page.PageUrl.Contains('#'))
        {
            SetCounts(row, coverage);
            row.UsageAttributionState = "CoveredFileNoEvents";
        }
        return row;
    }

    private static void SetCoverage(LegacyClassicPageCsvRow row, ClassicPageAuditUsage usage)
    {
        row.UsageQueryStatus = usage.QueryStatus?.Trim().ToLowerInvariant() switch
        {
            "succeeded" => "succeeded",
            "partial" => "partial",
            "failed" => "failed",
            "skipped" => "skipped",
            _ => "notcollected",
        };
        row.AuditWindowStart = ConcreteDate(usage.AuditWindowStart);
        row.AuditWindowEnd = ConcreteDate(usage.AuditWindowEnd);
    }

    private static void SetCounts(LegacyClassicPageCsvRow row, ClassicPageAuditUsage usage)
    {
        row.AuditViewsCount = usage.AuditViewsCount;
        row.AuditCreatesCount = usage.AuditCreatesCount;
        row.AuditEditsCount = usage.AuditEditsCount;
        row.AuditUniqueUsers = usage.AuditUniqueUsers;
    }

    private static DateTime? ConcreteDate(DateTime value) =>
        value == DateTime.MinValue || value == DateTime.MaxValue ? null : value;

    private static bool HasLocator(ClassicPageAuditUsage usage) =>
        !string.IsNullOrEmpty(usage.SiteUrl) && !string.IsNullOrEmpty(usage.PageUrl);

    private static bool IsSiteSummary(ClassicPageAuditUsage usage) =>
        usage.WebUrl == "/" && Uri.TryCreate(usage.SiteUrl, UriKind.Absolute, out Uri? site) &&
        (site.Scheme == Uri.UriSchemeHttps || site.Scheme == Uri.UriSchemeHttp) &&
        string.Equals(usage.PageUrl.TrimEnd('/'), site.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal);

    private static bool HasItemLocator(ClassicPage page)
    {
        int queryStart = page.PageUrl.IndexOf('?');
        int queryEnd = page.PageUrl.IndexOf('#');
        if (queryEnd < 0) queryEnd = page.PageUrl.Length;
        if (queryStart < 0 || queryStart >= queryEnd) return false;
        bool found = false;
        foreach (string part in page.PageUrl[(queryStart + 1)..queryEnd].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = part.Split('=', 2);
            try
            {
                if (!Uri.UnescapeDataString(pair[0]).Equals("ID", StringComparison.OrdinalIgnoreCase)) continue;
                if (pair.Length != 2 || found || !int.TryParse(Uri.UnescapeDataString(pair[1]), NumberStyles.None,
                        CultureInfo.InvariantCulture, out int itemId) || itemId <= 0 ||
                    (page.ListItemId.HasValue && itemId != page.ListItemId.Value))
                    return false;
                found = true;
            }
            catch (UriFormatException)
            {
                return false;
            }
        }
        return found;
    }
}
