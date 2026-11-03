namespace PnP.Scanning.Core.Discovery;

/// <summary>
/// Normalizes and classifies the display-form URLs through which a list item that owns no file of its
/// own is addressed.
/// </summary>
/// <remarks>
/// The two display form tiers are not stored in the same shape. A list's <c>DefaultDisplayFormUrl</c>
/// comes back server-relative (occasionally absolute), but a content type's <c>DisplayFormUrl</c> is
/// returned exactly as the content type definition wrote it: web-relative ("Lists/Posts/Post.aspx", the
/// stock blog Post content type) or behind one of the tokens
/// <c>ListFormWebPart.ComputeServerRelativePageUrl</c> expands. Both are normalized to a server-relative
/// URL here so the tiers can be compared, matched against the stock form and stored as one page location.
/// </remarks>
internal static class DisplayFormUrlPolicy
{
    // "~sitecollectionlayouts/" is tested before "~sitecollection/" because it also starts with it.
    private static readonly (string Token, bool SiteScoped, bool Layouts)[] FormUrlTokens =
    {
        ("~sitecollectionlayouts/", true, true),
        ("~sitecollection/", true, false),
        ("~layouts/", false, true),
        ("~site/", false, false),
    };

    private const string LayoutsFolder = "_layouts/15";

    /// <summary>
    /// Normalizes a display-form URL to a server-relative path. An unrecognized token is not a path, so
    /// it yields <c>null</c> instead of a guessed location.
    /// </summary>
    internal static string ToServerRelative(string formUrl, string webServerRelativeUrl,
        string siteServerRelativeUrl)
    {
        if (string.IsNullOrWhiteSpace(formUrl))
        {
            return null;
        }

        string value = formUrl.Trim();

        if (Uri.TryCreate(value, UriKind.Absolute, out Uri absolute) &&
            (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            return absolute.AbsolutePath;
        }

        if (value.StartsWith('/'))
        {
            return value;
        }

        foreach (var token in FormUrlTokens)
        {
            if (value.StartsWith(token.Token, StringComparison.OrdinalIgnoreCase))
            {
                string root = token.SiteScoped ? siteServerRelativeUrl : webServerRelativeUrl;
                return CombineServerRelative(token.Layouts ? CombineServerRelative(root, LayoutsFolder) : root,
                    value[token.Token.Length..]);
            }
        }

        return value.StartsWith('~') ? null : CombineServerRelative(webServerRelativeUrl, value);
    }

    private static string CombineServerRelative(string root, string relative)
    {
        string left = string.IsNullOrWhiteSpace(root) ? string.Empty : root.TrimEnd('/');
        string right = relative.TrimStart('/');
        return right.Length == 0 ? (left.Length == 0 ? "/" : left) : $"{left}/{right}";
    }

    internal static bool IsContentPageForm(string formUrl) => formUrl != null && !IsStockListForm(formUrl);

    /// <summary>
    /// Recognizes the stock list form, whose items are ordinary data rows rather than content pages.
    /// </summary>
    /// <remarks>
    /// Heuristic. The server truth is the form's ghosted setup path ("pages\form.aspx"), which the list
    /// and content type APIs do not expose; the client only sees the resolved form URL. A stock form is
    /// therefore recognized by its leaf name or by living under _layouts. The limitation is twofold: a
    /// customized form that kept the name DispForm.aspx is treated as stock (its items are skipped), and
    /// a stock form reached through a content type override that renamed it is treated as a content page.
    /// Because a list's own DefaultDisplayFormUrl is DispForm.aspx for all but a handful of templates,
    /// the content type tier is in practice the only one that ever names a content page form, which is
    /// why its URL must be normalized rather than discarded. This becomes exact once the form file's
    /// setup path is collected: a form still ghosted to "pages\form.aspx" is the stock form regardless
    /// of its name, and anything else is a real page.
    /// </remarks>
    internal static bool IsStockListForm(string formUrl)
    {
        string path = formUrl.Split('?')[0];
        return path.Contains("/_layouts/", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(Path.GetFileName(path), "DispForm.aspx", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Picks an item's display form: its content type's form when one matches, else the list default.
    /// </summary>
    /// <remarks>
    /// <paramref name="contentTypeForms"/> must be ordered longest content type id first, so the most
    /// specific type in the id hierarchy wins over its ancestor.
    /// </remarks>
    internal static string Resolve(IEnumerable<(string ContentTypeId, string FormUrl)> contentTypeForms,
        string listForm, string itemContentTypeId)
    {
        if (!string.IsNullOrEmpty(itemContentTypeId) && contentTypeForms != null)
        {
            foreach (var candidate in contentTypeForms)
            {
                if (itemContentTypeId.StartsWith(candidate.ContentTypeId, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate.FormUrl;
                }
            }
        }

        return listForm;
    }
}
