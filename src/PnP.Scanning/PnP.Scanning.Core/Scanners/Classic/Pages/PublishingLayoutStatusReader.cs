using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.Scanners;

internal sealed class PublishingLayoutStatusReader
{
    private readonly Uri webUrl;
    private readonly Func<string, Task<string>> readStatus;
    private readonly Dictionary<string, Task<string>> statuses = new(StringComparer.OrdinalIgnoreCase);

    internal PublishingLayoutStatusReader(Uri webUrl, Func<string, Task<string>> readStatus)
    {
        this.webUrl = webUrl;
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

        // Post-scanning runs after all Web workers commit, including the root gallery.
        var discovered = await db.ClassicPageDiscoveries.Where(row => row.ScanId == scanId &&
            row.SiteUrl == siteUrl && row.RowType == "Page")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var byUrl = discovered.Where(row => !string.IsNullOrWhiteSpace(row.Url))
            .ToLookup(row => row.Url, StringComparer.OrdinalIgnoreCase);
        var siteUri = new Uri(siteUrl);
        var origin = siteUri.GetLeftPart(UriPartial.Authority);
        var reader = new PublishingLayoutStatusReader(siteUri, async url =>
        {
            var knownStatuses = byUrl[url].Concat(byUrl[origin + url])
                .Select(row => row.CustomizedPageStatus)
                .Where(status => !string.IsNullOrWhiteSpace(status))
                .Distinct(StringComparer.Ordinal).ToArray();
            if (knownStatuses.Length > 1)
                throw new InvalidDataException("Discovery returned conflicting customization statuses for layout " + url);
            if (knownStatuses.Length == 1) return knownStatuses[0];

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
                page.AssessmentStatus = "Failed";
                foreach (var row in byUrl[page.PageUrl].Where(row =>
                    string.Equals(row.WebUrl, page.WebUrl, StringComparison.OrdinalIgnoreCase)))
                {
                    row.AssessmentStatus = "Failed";
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
        if (page.PageType != PageScanComponent.PublishingPage)
        {
            page.LayoutUrl = null;
            return;
        }
        if (string.IsNullOrWhiteSpace(page.LayoutUrl)) return;

        var url = ResolveServerRelativeUrl(page.LayoutUrl);
        if (!statuses.TryGetValue(url, out var status))
        {
            // Cache failures too: a denied/missing shared layout must not trigger one request per page.
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
        if (url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal) && !url.Contains('\\'))
        {
            return url;
        }
        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute) &&
            absolute.Scheme == webUrl.Scheme && absolute.Authority.Equals(webUrl.Authority, StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrEmpty(absolute.Query) && string.IsNullOrEmpty(absolute.Fragment))
        {
            return absolute.AbsolutePath;
        }
        throw new InvalidDataException("Publishing page layout must have a server-relative URL or an absolute URL on the scanned origin.");
    }
}
