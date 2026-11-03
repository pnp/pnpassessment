namespace PnP.Scanning.Core.Discovery;

/// <summary>
/// Normalizes list and registered content-type display-form declarations.
/// </summary>
/// <remarks>
/// The two display form tiers are not stored in the same shape. A list's <c>DefaultDisplayFormUrl</c>
/// comes back server-relative (occasionally absolute), but a content type's <c>DisplayFormUrl</c> is
/// returned exactly as the content type definition wrote it: web-relative ("Lists/Posts/Post.aspx", the
/// stock blog Post content type) or behind one of the tokens
/// <c>ListFormWebPart.ComputeServerRelativePageUrl</c> expands. Both are normalized to a server-relative
/// URL here so both declarations can be stored in a consistent form.
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

}
