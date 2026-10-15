using Microsoft.SharePoint.Client;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.Scanners;

internal sealed class PublishingLayoutStatusReader
{
    private readonly Uri webUrl;
    private readonly Func<string, Task<string>> readStatus;
    private readonly Dictionary<string, Task<string>> statuses = new(StringComparer.OrdinalIgnoreCase);

    internal PublishingLayoutStatusReader(ClientContext context)
        : this(new Uri(context.Url), async url =>
        {
            var file = context.Site.RootWeb.GetFileByServerRelativeUrl(url);
            context.Load(file, value => value.CustomizedPageStatus);
            await context.ExecuteQueryAsync().ConfigureAwait(false);
            return file.CustomizedPageStatus.ToString();
        })
    {
    }

    internal PublishingLayoutStatusReader(Uri webUrl, Func<string, Task<string>> readStatus)
    {
        this.webUrl = webUrl;
        this.readStatus = readStatus;
    }

    internal async Task EnrichAsync(ClassicPage page)
    {
        page.PageLayoutCustomizedPageStatus = null;
        if (page.PageType != PageScanComponent.PublishingPage)
        {
            page.PageLayoutUrl = null;
            return;
        }
        if (string.IsNullOrWhiteSpace(page.PageLayoutUrl)) return;

        var url = ResolveServerRelativeUrl(page.PageLayoutUrl);
        if (!statuses.TryGetValue(url, out var status))
        {
            // Cache failures too: a denied/missing shared layout must not trigger one request per page.
            status = ReadNormalizedStatusAsync(url);
            statuses.Add(url, status);
        }
        page.PageLayoutCustomizedPageStatus = await status.ConfigureAwait(false);
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
