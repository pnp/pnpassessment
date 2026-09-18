using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.Discovery;

/// <summary>
/// Native-scan persistence. Network work happens outside the writer gate. A fresh context per
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
                        row.ErrorStage = Join(previous.ErrorStage, row.ErrorStage);
                        row.ErrorCodes = Join(previous.ErrorCodes, row.ErrorCodes);
                        row.ErrorDetail = Join(previous.ErrorDetail, row.ErrorDetail, "\n");
                    }
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

    internal static string Join(string left, string right, string separator = ";")
    {
        var values = new[] { left, right }.Where(value => !string.IsNullOrWhiteSpace(value));
        // Preserve complete human-readable messages, including embedded newlines and commas.
        if (separator == "\n") return string.Join(separator, values.Distinct(StringComparer.Ordinal));
        return string.Join(separator, values.SelectMany(value => value.Split(separator, StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal));
    }

    internal async Task<string> ReadWebCoverageAsync(Guid scanId, string siteUrl, string webUrl)
    {
        using var db = createContext();
        var incomplete = await db.ClassicPageDiscoveries.AsNoTracking().AnyAsync(row => row.ScanId == scanId &&
            row.SiteUrl == siteUrl && row.WebUrl == webUrl && row.RowType == "Scope" &&
            (row.ObservationMethod == null || row.ObservationMethod != "WebScan") &&
            row.DiscoveryStatus != "Complete" && row.DiscoveryStatus != "Empty" && row.DiscoveryStatus != "PolicyExcluded");
        return incomplete ? "Partial" : "Complete";
    }
}
