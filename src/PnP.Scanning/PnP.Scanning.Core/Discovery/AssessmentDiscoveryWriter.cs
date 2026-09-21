using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Storage;
using System.Text.Json;

namespace PnP.Scanning.Core.Discovery;

/// <summary>
/// Classic-assessment acquisition persistence. Network work happens outside the writer gate. A fresh context per
/// commit avoids sharing EF/SQLite state between the existing TPL Web workers.
/// </summary>
internal sealed class AssessmentDiscoveryWriter
{
    private static readonly SemaphoreSlim WriteGate = new(1, 1);
    private readonly Func<ScanContext> createContext;

    internal AssessmentDiscoveryWriter(Guid scanId) : this(() => new ScanContext(scanId)) { }
    internal AssessmentDiscoveryWriter(Func<ScanContext> createContext) => this.createContext = createContext;

    internal async Task WriteAsync(IEnumerable<ClassicPageDiscovery> rows, CancellationToken cancellationToken = default)
    {
        await WriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var db = createContext();
            foreach (var row in rows)
            {
                var previous = await db.ClassicPageDiscoveries.FindAsync(
                    new object[] { row.ScanId, row.RecordKey }, cancellationToken).ConfigureAwait(false);
                if (previous == null) db.ClassicPageDiscoveries.Add(row);
                else
                {
                    // Observing the same file through Forms/Views and raw files must not erase
                    // richer metadata previously read through its list-item surface.
                    if (row.RowType == "Page")
                    {
                        var changed = new List<string>();
                        if (!string.Equals(previous.Url, row.Url, StringComparison.OrdinalIgnoreCase)) changed.Add("Url");
                        if (previous.ListId.HasValue && row.ListId.HasValue && previous.ListId != row.ListId) changed.Add("ListId");
                        if (previous.ListItemId.HasValue && row.ListItemId.HasValue && previous.ListItemId != row.ListItemId) changed.Add("ListItemId");
                        if (previous.HomePage.HasValue && row.HomePage.HasValue && previous.HomePage != row.HomePage) changed.Add("HomePage");
                        if (previous.ContentTypeId != null && row.ContentTypeId != null && previous.ContentTypeId != row.ContentTypeId) changed.Add("ContentTypeId");
                        if (changed.Count != 0)
                            AssessmentWebDiscovery.AddError(row, "DiscoveryMetadata", DiscoveryGapCodes.ChangedDuringScan,
                                "Repeated file identity changed: " + string.Join(", ", changed));
                        row.ListId ??= previous.ListId;
                        row.FolderUniqueId ??= previous.FolderUniqueId;
                        row.ListItemId ??= previous.ListItemId;
                        row.ContentTypeId ??= previous.ContentTypeId;
                        row.PageType ??= previous.PageType;
                        row.HomePage ??= previous.HomePage;
                        row.LibraryHidden ??= previous.LibraryHidden;
                        row.AssessmentStatus ??= previous.AssessmentStatus;
                    }
                    else if (row.RowType == "Reference")
                    {
                        if (previous.FileUniqueId.HasValue && row.FileUniqueId.HasValue &&
                            previous.FileUniqueId != row.FileUniqueId)
                        {
                            row.DiscoveryStatus = "Unknown";
                            AssessmentWebDiscovery.AddError(row, "ReferenceReconciliation",
                                DiscoveryGapCodes.MetadataConflict,
                                "Repeated reference identity resolved to a different physical file.");
                            row.FileUniqueId = previous.FileUniqueId;
                        }
                        row.FileUniqueId ??= previous.FileUniqueId;
                    }
                    else if (row.RowType == "Pagination" && previous.EvidenceJson != null &&
                             row.EvidenceJson != null && previous.EvidenceJson != row.EvidenceJson)
                    {
                        row.DiscoveryStatus = "Unknown";
                        AssessmentWebDiscovery.AddError(row, "PaginationReconciliation",
                            DiscoveryGapCodes.ChangedDuringScan,
                            "Repeated pagination evidence changed for the same request identity.");
                    }
                    else if (row.RowType == "Scope" && row.ScopeType == "Surface" &&
                             !TerminalSuccess(previous.DiscoveryStatus) && TerminalSuccess(row.DiscoveryStatus))
                    {
                        // A later successful visit cannot erase a retained denied/failed/unknown surface.
                        row.DiscoveryStatus = previous.DiscoveryStatus;
                        row.EvidenceJson = previous.EvidenceJson;
                    }
                    row.ErrorStage = Join(previous.ErrorStage, row.ErrorStage);
                    row.ErrorCodes = Join(previous.ErrorCodes, row.ErrorCodes);
                    row.ErrorDetail = Join(previous.ErrorDetail, row.ErrorDetail, "\n");
                    row.EvidenceJson ??= previous.EvidenceJson;
                    db.Entry(previous).CurrentValues.SetValues(row);
                }
            }
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { WriteGate.Release(); }
    }

    internal async Task<List<ClassicPageDiscovery>> ReadPagesAsync(Guid scanId, string siteUrl, string webUrl,
        CancellationToken cancellationToken = default)
    {
        using var db = createContext();
        return await db.ClassicPageDiscoveries.AsNoTracking().Where(row => row.ScanId == scanId &&
            row.SiteUrl == siteUrl && row.WebUrl == webUrl && row.RowType == "Page")
            .OrderBy(row => row.RecordKey).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task UpdateExistingAsync(IEnumerable<ClassicPageDiscovery> rows,
        CancellationToken cancellationToken = default)
    {
        await WriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var db = createContext();
            db.ClassicPageDiscoveries.UpdateRange(rows);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { WriteGate.Release(); }
    }

    internal async Task<int> FailUnassessedPagesAsync(Guid scanId, string siteUrl, string webUrl,
        Exception error, string stage)
    {
        // This is failure finalization, not discovery: retain file identities and existence,
        // and never overwrite pages that already reached an assessment disposition.
        await WriteGate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var db = createContext();
            var unfinished = await db.ClassicPageDiscoveries.Where(row => row.ScanId == scanId &&
                row.SiteUrl == siteUrl && row.WebUrl == webUrl && row.RowType == "Page" &&
                (row.AssessmentStatus == null || row.AssessmentStatus == "")).ToListAsync().ConfigureAwait(false);
            foreach (var row in unfinished)
            {
                row.AssessmentStatus = "Failed";
                AssessmentWebDiscovery.AddError(row, stage, AssessmentWebDiscovery.ErrorCode(error),
                    "Page assessment did not finish because its Web failed: " + error.GetBaseException().Message);
            }
            await db.SaveChangesAsync().ConfigureAwait(false);
            return unfinished.Count;
        }
        finally { WriteGate.Release(); }
    }

    internal async Task<DiscoveryVerdict> FinalizeScanAsync(Guid scanId,
        CancellationToken cancellationToken = default)
    {
        await WriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var db = createContext();
            var allRows = await db.ClassicPageDiscoveries.AsNoTracking()
                .Where(row => row.ScanId == scanId)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var coverageRows = allRows.Where(row => row.RowType is "Scope" or "Gap").ToArray();
            var scopes = coverageRows.Where(row => row.RowType == "Scope").ToArray();
            var selection = scopes.FirstOrDefault(row => row.RecordKey == "assessment:site-selection");
            var pageSelection = scopes.FirstOrDefault(row => row.RecordKey == "assessment:page-selection");
            var statuses = coverageRows.Select(row => row.DiscoveryStatus)
                .Concat(allRows.Where(row => row.RowType == "Reference" ||
                    (row.RowType == "Pagination" && row.DiscoveryStatus is ("Denied" or "Failed" or "Unknown")))
                    .Select(row => row.DiscoveryStatus)).ToArray();
            var gapCodes = allRows.SelectMany(row => (row.ErrorCodes ?? string.Empty)
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Select(code => code.Contains(':') ? code[(code.LastIndexOf(':') + 1)..] : code)
                .ToArray();

            DiscoveryVerdict verdict;
            if (scopes.Length == 0 || selection?.ScopeType is not ("Tenant" or "SiteSelection") ||
                statuses.Any(status => status is "Pending" or "Unknown") ||
                gapCodes.Any(DiscoveryGapCodes.ForcesUnknown))
            {
                verdict = DiscoveryVerdict.Unknown;
            }
            else if (statuses.Any(status => status is "Denied" or "Failed" or "Partial" or "Cancelled") ||
                     gapCodes.Contains(DiscoveryGapCodes.ExpectedChildMissing, StringComparer.Ordinal))
            {
                verdict = DiscoveryVerdict.Incomplete;
            }
            else if (pageSelection?.ObservationMethod == "HomePageOnly" || selection?.ScopeType == "SiteSelection")
            {
                verdict = DiscoveryVerdict.CompleteDeclaredSubset;
            }
            else
            {
                verdict = DiscoveryVerdict.CompleteTenantVerified;
            }

            var pageCount = allRows.Count(row => row.RowType == "Page");
            var gapCount = coverageRows.Count(row => row.DiscoveryStatus is not ("Complete" or "Empty" or "PolicyExcluded"));
            var summary = await db.ClassicPageDiscoveries.FindAsync(
                new object[] { scanId, "summary:coverage" }, cancellationToken).ConfigureAwait(false);
            var current = new ClassicPageDiscovery
            {
                ScanId = scanId,
                RecordKey = "summary:coverage",
                RowType = "Summary",
                ScopeType = "Assessment",
                Url = selection?.Url,
                ObservationMethod = pageSelection?.ObservationMethod ?? selection?.ObservationMethod,
                DiscoveryStatus = verdict.ToString(),
                ExpectedChildCount = selection?.ExpectedChildCount,
                ObservedChildCount = pageCount,
                ErrorCodes = string.Join(';', gapCodes.Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)),
                EvidenceJson = JsonSerializer.Serialize(new
                {
                    verdict = verdict.ToString(),
                    declaredScope = selection?.ScopeType == "SiteSelection" || pageSelection != null,
                    pageScope = pageSelection?.ObservationMethod ?? AspxDiscoveryIntent.FullInventory.ToString(),
                    siteCount = selection?.ObservedChildCount,
                    pageCount,
                    scopeCount = scopes.Length,
                    incompleteScopeCount = gapCount,
                }),
                ObservedAtUtc = DateTime.UtcNow,
            };
            if (summary == null) db.ClassicPageDiscoveries.Add(current);
            else db.Entry(summary).CurrentValues.SetValues(current);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return verdict;
        }
        finally { WriteGate.Release(); }
    }

    internal static string Join(string left, string right, string separator = ";")
    {
        var values = new[] { left, right }.Where(value => !string.IsNullOrWhiteSpace(value));
        // Preserve complete human-readable messages, including embedded newlines and commas.
        if (separator == "\n") return string.Join(separator, values.Distinct(StringComparer.Ordinal));
        return string.Join(separator, values.SelectMany(value => value.Split(separator, StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal));
    }

    private static bool TerminalSuccess(string status) =>
        status is "Complete" or "Empty" or "PolicyExcluded" or "Discovered";

    internal async Task<string> ReadWebCoverageAsync(Guid scanId, string siteUrl, string webUrl)
    {
        using var db = createContext();
        var incomplete = await db.ClassicPageDiscoveries.AsNoTracking().AnyAsync(row => row.ScanId == scanId &&
            row.SiteUrl == siteUrl && row.WebUrl == webUrl && (row.RowType == "Scope" || row.RowType == "Gap") &&
            (row.ObservationMethod == null || row.ObservationMethod != "WebScan") &&
            row.DiscoveryStatus != "Complete" && row.DiscoveryStatus != "Empty" && row.DiscoveryStatus != "PolicyExcluded");
        return incomplete ? "Partial" : "Complete";
    }
}
