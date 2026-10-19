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
        var urls = new HashSet<string>(pages.Select(page => page.PageUrl), StringComparer.OrdinalIgnoreCase);
        foreach (var page in pages)
        {
            if (!TryResolveServerRelativeUrl(siteUri, page.LayoutUrl, out var url)) continue;
            urls.Add(url);
            urls.Add(origin + url);
        }
        var requestedUrls = urls.ToArray();

        // Post-scanning runs after all Web workers commit, including the root gallery.
        var discovered = await db.ClassicPageDiscoveries.Where(row => row.ScanId == scanId &&
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
                foreach (var row in byUrl[page.PageUrl].Where(row =>
                    string.Equals(row.WebUrl, page.WebUrl, StringComparison.OrdinalIgnoreCase)))
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
            // The containment checks below inspect a single decode, so a multiply-encoded
            // reference such as %252e%252e would satisfy them and still escape the site
            // collection once SharePoint decodes the value again. Require decoding to be
            // idempotent so that what is validated here is what the file APIs receive.
            if (!string.Equals(Uri.UnescapeDataString(path), path, StringComparison.Ordinal)) return false;
            var sitePath = Uri.UnescapeDataString(siteUrl.AbsolutePath).TrimEnd('/');
            if (path.StartsWith(sitePath + "/", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains('\\') && !path.Split('/').Any(segment => segment is "." or ".."))
            {
                // Discovery stores SharePoint's unencoded ServerRelativeUrl and CSOM's
                // GetFileByServerRelativeUrl treats an escape sequence as literal text, so
                // both consumers need the decoded spelling rather than AbsolutePath.
                resolved = path;
                return true;
            }
        }
        return false;
    }
}
