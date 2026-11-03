using System.Net;

namespace PnP.Scanning.Core.Discovery;

/// <summary>Validates tenant-resource references separately from the stricter continuation-URI contract.</summary>
internal sealed class SharePointAspxUrlPolicy
{
    private const string HttpsScheme = "https";
    private const string LayoutsRoot = "/_layouts/";
    private const string AspxExtension = ".aspx";
    private const string EncodedForwardSlash = "%2f";
    private const string EncodedBackslash = "%5c";
    private const string CurrentDirectorySegment = ".";
    private const string ParentDirectorySegment = "..";
    private const string FilterQueryParameter = "$filter";
    private readonly Uri siteUrl;
    private readonly Uri webUrl;

    /// <summary>Captures the already-validated current Web and site-collection boundaries without acquiring a client.</summary>
    internal SharePointAspxUrlPolicy(Uri siteUrl, Uri webUrl)
    {
        this.siteUrl = siteUrl;
        this.webUrl = webUrl;
    }

    /// <summary>
    /// Resolves a resource reference to its server-relative path, rejecting foreign authorities, traversal and encoded separators.
    /// Layout references are allowed for catalog resolution; other paths must remain inside this site collection.
    /// </summary>
    internal bool TryNormalizeResourceReference(string reference, out string path)
    {
        path = null;
        if (string.IsNullOrWhiteSpace(reference) || reference.Contains('\\') ||
            reference.Contains(EncodedForwardSlash, StringComparison.OrdinalIgnoreCase) ||
            reference.Contains(EncodedBackslash, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string rawPath = reference.Split('?', '#')[0];
        if (ContainsTraversalSegment(rawPath))
        {
            return false;
        }

        var baseUri = new Uri(webUrl.AbsoluteUri.TrimEnd('/') + "/");
        if (!Uri.TryCreate(baseUri, reference, out var resourceUri) ||
            !HasSameHttpsAuthority(resourceUri, siteUrl) || !string.IsNullOrEmpty(resourceUri.UserInfo))
        {
            return false;
        }

        string decoded = Uri.UnescapeDataString(resourceUri.AbsolutePath);
        if (decoded.Any(char.IsControl) || ContainsTraversalSegment(decoded) ||
            decoded.Contains(EncodedForwardSlash, StringComparison.OrdinalIgnoreCase) ||
            decoded.Contains(EncodedBackslash, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!decoded.StartsWith(LayoutsRoot, StringComparison.OrdinalIgnoreCase) &&
            !IsPathWithinRoot(resourceUri.AbsolutePath, siteUrl.AbsolutePath))
        {
            return false;
        }

        path = resourceUri.AbsolutePath;
        return true;
    }

    /// <summary>
    /// Encodes a server-observed decoded physical ResourcePath one segment at a time.
    /// Literal percent sequences and hash/query delimiters are file-name data, not URI escapes or reference navigation.
    /// </summary>
    internal bool TryNormalizeDecodedPhysicalPath(string locator, out string path)
    {
        path = null;
        if (string.IsNullOrWhiteSpace(locator) || locator.Any(char.IsControl) || locator.Contains((char)92))
        {
            return false;
        }

        string decodedPath = locator;
        if (!decodedPath.StartsWith("/", StringComparison.Ordinal))
        {
            const string authoritySeparator = "://";
            int schemeEnd = locator.IndexOf(authoritySeparator, StringComparison.Ordinal);
            int pathStart = schemeEnd < 0 ? -1 : locator.IndexOf('/', schemeEnd + authoritySeparator.Length);
            if (pathStart < 0 || !Uri.TryCreate(locator[..pathStart] + "/", UriKind.Absolute, out var authority) ||
                !HasSameHttpsAuthority(siteUrl, authority) || !string.IsNullOrEmpty(authority.UserInfo))
            {
                return false;
            }
            decodedPath = locator[pathStart..];
        }

        if (decodedPath.StartsWith("//", StringComparison.Ordinal) || ContainsTraversalSegment(decodedPath) ||
            !IsPathWithinRoot(decodedPath, Uri.UnescapeDataString(siteUrl.AbsolutePath)))
        {
            return false;
        }

        path = string.Join('/', decodedPath.Split('/').Select(Uri.EscapeDataString));
        return true;
    }

    /// <summary>
    /// Accepts only a same-authority continuation of the original collection path and all original query values.
    /// A newly introduced nonempty filter, duplicate query key, fragment or user-info component is not followed.
    /// </summary>
    internal static bool TryValidateContinuationUri(Uri first, string continuation, out Uri next)
    {
        next = null;
        if (!Uri.TryCreate(first, continuation, out var candidate) || !HasSameHttpsAuthority(first, candidate) ||
            !string.IsNullOrEmpty(candidate.UserInfo) || !string.IsNullOrEmpty(candidate.Fragment) ||
            !string.Equals(first.AbsolutePath, candidate.AbsolutePath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var expected = ReadQueryParameters(first);
        var actual = ReadQueryParameters(candidate);
        if (actual == null || expected == null ||
            actual.TryGetValue(FilterQueryParameter, out var filter) && !string.IsNullOrWhiteSpace(filter))
        {
            return false;
        }

        foreach (var queryParameter in expected)
        {
            if (!actual.TryGetValue(queryParameter.Key, out var value) || value != queryParameter.Value)
            {
                return false;
            }
        }

        next = candidate;
        return true;
    }

    /// <summary>Requires an exact default-port HTTPS Web authority before any authentication or folder request.</summary>
    internal static void ValidateWeb(Uri web)
    {
        if (web == null || !web.IsAbsoluteUri || web.Scheme != HttpsScheme || !web.IsDefaultPort ||
            !string.IsNullOrEmpty(web.UserInfo) || !string.IsNullOrEmpty(web.Query) || !string.IsNullOrEmpty(web.Fragment))
        {
            throw new ArgumentException("An exact HTTPS web authority is required.");
        }
    }

    /// <summary>Requires HTTPS on the default port and the same authority, without normalizing one resource into another.</summary>
    internal static bool HasSameHttpsAuthority(Uri expected, Uri actual)
    {
        return expected != null && actual != null && expected.Scheme == HttpsScheme && actual.Scheme == HttpsScheme &&
            expected.IsDefaultPort && actual.IsDefaultPort &&
            string.Equals(expected.Authority, actual.Authority, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Checks an exact root or slash-delimited descendant; a tenant-root Web admits every server-relative path.</summary>
    internal static bool IsPathWithinRoot(string path, string root)
    {
        return root.TrimEnd('/') == string.Empty ||
            path.TrimEnd('/').Equals(root.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(root.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Recognizes an ASPX file suffix without using a page-family classification to control discovery.</summary>
    internal static bool IsAspxPath(string path)
    {
        return path != null && path.EndsWith(AspxExtension, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Identifies layout references that must be resolved through explicit catalog membership.</summary>
    internal static bool IsLayoutsPath(string path)
    {
        return path.StartsWith(LayoutsRoot, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Rejects explicit current-directory and parent-directory segments before URI resolution can erase them.</summary>
    private static bool ContainsTraversalSegment(string path)
    {
        return path.Split('/').Any(segment => segment is CurrentDirectorySegment or ParentDirectorySegment);
    }

    /// <summary>Decodes a collection query once and returns unknown when a key is duplicated.</summary>
    private static Dictionary<string, string> ReadQueryParameters(Uri uri)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = part.IndexOf('=');
            string key = WebUtility.UrlDecode(separator < 0 ? part : part[..separator]);
            string value = WebUtility.UrlDecode(separator < 0 ? string.Empty : part[(separator + 1)..]);
            if (!parameters.TryAdd(key, value))
            {
                return null;
            }
        }

        return parameters;
    }
}
