using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.Scanners;

internal sealed class PublishingLayoutStatusReader
{
    private const int FailureLookupBatchSize = 256;
    private readonly Uri siteUrl;
    private readonly Func<string, Task<string>> readStatus;
    private readonly Dictionary<string, Task<string>> statuses = new(StringComparer.OrdinalIgnoreCase);

    internal PublishingLayoutStatusReader(Uri siteUrl, Func<string, Task<string>> readStatus)
    {
        this.siteUrl = siteUrl;
        this.readStatus = readStatus;
    }

    internal static async Task EnrichSiteAsync(ScanContext db, Guid scanId, string siteUrl,
        Func<string, Task<string>> readMissingStatus, Action<Exception, ClassicPage> logFailure,
        CancellationToken cancellationToken = default)
    {
        var pages = await db.ClassicPages.Where(page => page.ScanId == scanId &&
            page.SiteUrl == siteUrl && page.PageType == PageScanComponent.PublishingPage)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (pages.Count == 0) return;

        var siteUri = new Uri(siteUrl);
        var origin = siteUri.GetLeftPart(UriPartial.Authority);
        // Only the distinct layouts are read up front. Seeding this with every page URL as well
        // would size the prefetch - the IN-list parameter and the tracked result alike - by the
        // page count of the site collection instead of by the handful of layouts it shares.
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in pages)
        {
            if (!TryResolveServerRelativeUrl(siteUri, page.LayoutUrl, out var url)) continue;
            urls.Add(url);
            urls.Add(origin + url);
        }
        var requestedUrls = urls.ToArray();

        // Post-scanning runs after all Web workers commit, including the root gallery.
        var discovered = requestedUrls.Length == 0
            ? new List<ClassicPageDiscovery>()
            : await db.ClassicPageDiscoveries.Where(row => row.ScanId == scanId &&
                row.SiteUrl == siteUrl && row.RowType == "Page" &&
                requestedUrls.Contains(EF.Functions.Collate(row.Url, "NOCASE")))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        var byUrl = discovered.Where(row => !string.IsNullOrWhiteSpace(row.Url))
            .ToLookup(row => row.Url, StringComparer.OrdinalIgnoreCase);
        var reader = new PublishingLayoutStatusReader(siteUri, async url =>
        {
            var observations = byUrl[url].Concat(byUrl[origin + url])
                .Where(row => !string.IsNullOrWhiteSpace(row.CustomizedPageStatus))
                .OrderByDescending(row => row.ObservedAtUtc)
                .ThenBy(row => row.RecordKey, StringComparer.Ordinal).ToArray();
            if (observations.Length > 0)
            {
                var latest = observations[0];
                if (observations.Any(row => row.CustomizedPageStatus != latest.CustomizedPageStatus))
                    AssessmentWebDiscovery.AddError(latest, "DiscoveryMetadata", DiscoveryGapCodes.ChangedDuringScan,
                        $"Layout {url} changed customization status during discovery; retained the latest observed value {latest.CustomizedPageStatus}.");
                return latest.CustomizedPageStatus;
            }

            // HomePageOnly and incomplete discovery may not have observed the gallery.
            return await readMissingStatus(url).ConfigureAwait(false);
        });

        var failures = new List<LayoutFailure>();
        foreach (var page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await reader.EnrichAsync(page).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                failures.Add(new LayoutFailure(page.PageUrl, page.WebUrl,
                    AssessmentWebDiscovery.ErrorCode(ex), AssessmentWebDiscovery.ErrorDetail(ex)));
                logFailure(ex, page);
            }
        }
        await RecordFailuresAsync(db, scanId, siteUrl, failures, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task RecordFailuresAsync(ScanContext db, Guid scanId, string siteUrl,
        IReadOnlyList<LayoutFailure> failures, CancellationToken cancellationToken)
    {
        if (failures.Count == 0) return;

        var failuresByUrl = failures.ToLookup(failure => failure.PageUrl, StringComparer.OrdinalIgnoreCase);
        var failuresByRecordKey = new Dictionary<string, List<LayoutFailure>>(StringComparer.Ordinal);
        // URL/NOCASE lookups cannot use the existing primary key. Locate the failed identities in one
        // lightweight scan instead of rescanning the site for every page (or every URL chunk).
        var identities = db.ClassicPageDiscoveries.AsNoTracking()
            .Where(row => row.ScanId == scanId && row.SiteUrl == siteUrl && row.RowType == "Page")
            .Select(row => new { row.RecordKey, row.WebUrl, row.Url });
        await foreach (var row in identities.AsAsyncEnumerable().WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var matching = failuresByUrl[row.Url].Where(failure =>
                string.Equals(failure.WebUrl, row.WebUrl, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matching.Count > 0) failuresByRecordKey.Add(row.RecordKey, matching);
        }

        // Only failed rows are tracked, with bounded parameters and indexed (ScanId, RecordKey) reads.
        // The keys above are already site/type scoped; keep those checks in memory so SQLite does not
        // choose the broader site index over the primary key. Tracking also preserves pending layout errors.
        foreach (var keys in failuresByRecordKey.Keys.Chunk(FailureLookupBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = await db.ClassicPageDiscoveries
                .Where(row => row.ScanId == scanId && keys.Contains(row.RecordKey))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var row in rows)
            {
                if (row.SiteUrl != siteUrl || row.RowType != "Page") continue;
                foreach (var failure in failuresByRecordKey[row.RecordKey])
                    AssessmentWebDiscovery.AddError(row, "PageLayoutMetadata", failure.Code, failure.Detail);
            }
        }
    }

    private sealed record LayoutFailure(string PageUrl, string WebUrl, string Code, string Detail);

    internal async Task EnrichAsync(ClassicPage page)
    {
        page.LayoutCustomizedPageStatus = null;
        if (string.IsNullOrWhiteSpace(page.LayoutUrl)) return;

        var url = ResolveServerRelativeUrl(page.LayoutUrl);
        if (!statuses.TryGetValue(url, out var status))
        {
            // The fallback transport retries transient HTTP failures before this task faults.
            // Cache terminal/exhausted failures so shared layouts do not restart that retry budget.
            status = ReadNormalizedStatusAsync(url);
            statuses.Add(url, status);
        }
        page.LayoutCustomizedPageStatus = await status.ConfigureAwait(false);
    }

    private async Task<string> ReadNormalizedStatusAsync(string url)
    {
        return FileCustomizationStatus.Normalize(await readStatus(url).ConfigureAwait(false));
    }

    private string ResolveServerRelativeUrl(string url)
    {
        if (TryResolveServerRelativeUrl(siteUrl, url, out var resolved)) return resolved;
        throw new InvalidDataException("Publishing page layout must have a server-relative or absolute URL within the scanned site collection.");
    }

    private static bool TryResolveServerRelativeUrl(Uri siteUrl, string url, out string resolved)
    {
        resolved = null;
        if (string.IsNullOrWhiteSpace(url)) return false;
        Uri absolute = null;
        var validUrl = !url.StartsWith("//", StringComparison.Ordinal) && !url.Contains('\\') &&
            (url.StartsWith('/') ? Uri.TryCreate(siteUrl, url, out absolute) : Uri.TryCreate(url, UriKind.Absolute, out absolute));
        if (validUrl && absolute.Scheme == siteUrl.Scheme &&
            absolute.Authority.Equals(siteUrl.Authority, StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrEmpty(absolute.Query) && string.IsNullOrEmpty(absolute.Fragment))
        {
            var path = Uri.UnescapeDataString(absolute.AbsolutePath);
            var sitePath = Uri.UnescapeDataString(siteUrl.AbsolutePath).TrimEnd('/');
            if (path.StartsWith(sitePath + "/", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains('\\') && !path.Split('/').Any(segment => segment is "." or ".."))
            {
                resolved = absolute.AbsolutePath;
                return true;
            }
        }
        return false;
    }
}
