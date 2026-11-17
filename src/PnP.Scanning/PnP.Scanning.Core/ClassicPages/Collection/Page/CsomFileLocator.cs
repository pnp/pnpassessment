#nullable enable
namespace PnP.Scanning.Core.ClassicPages.Collection.Page;

/// <summary>
/// Addresses a file through CSOM from the server path that collection holds for it.
/// </summary>
internal static class CsomFileLocator
{
    /// <summary>
    /// Tells whether a server path holds a <c>#</c>, or a <c>%</c> that is part of a folder or file name, and is
    /// therefore passed to SharePoint as a decoded path. A path in which a <c>%</c> starts a URL escape such as
    /// <c>%20</c> keeps the request that reads escapes, which is how the home page of a web with a space in its
    /// address is stored in a home-page-only assessment.
    /// </summary>
    internal static bool NeedsDecodedPath(string serverRelativeUrl) =>
        serverRelativeUrl.Contains('#') ||
        (serverRelativeUrl.Contains('%') && Uri.UnescapeDataString(serverRelativeUrl) == serverRelativeUrl);

    /// <summary>
    /// Returns the file at a server path, addressed so that a <c>#</c> or <c>%</c> in a folder or file name is
    /// read as part of the name.
    /// </summary>
    internal static Microsoft.SharePoint.Client.File GetFile(
        Microsoft.SharePoint.Client.Web web,
        string serverRelativeUrl) =>
        NeedsDecodedPath(serverRelativeUrl)
            ? web.GetFileByServerRelativePath(
                Microsoft.SharePoint.Client.ResourcePath.FromDecodedUrl(serverRelativeUrl))
            : web.GetFileByServerRelativeUrl(serverRelativeUrl);
}
