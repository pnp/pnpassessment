using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.Scanners;

internal sealed class PublishingLayoutStatusReader
{
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

        foreach (var page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await reader.EnrichAsync(page).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // The page's own discovery rows carry this failure, and nothing else needs them,
                // so they are read on the failure path rather than prefetched for every page.
                var failed = await db.ClassicPageDiscoveries.Where(row => row.ScanId == scanId &&
                    row.SiteUrl == siteUrl && row.RowType == "Page" &&
                    EF.Functions.Collate(row.WebUrl, "NOCASE") == page.WebUrl &&
                    EF.Functions.Collate(row.Url, "NOCASE") == page.PageUrl)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                foreach (var row in failed)
                {
                    AssessmentWebDiscovery.AddError(row, "PageLayoutMetadata", AssessmentWebDiscovery.ErrorCode(ex),
                        AssessmentWebDiscovery.ErrorDetail(ex));
                }
                logFailure(ex, page);
            }
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

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
