using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Scanners;

namespace PnP.Scanning.Core.Pipeline.Analysis.Page;

/// <summary>
/// Reuses existing pure page classification and physical-URL validation rules.
/// </summary>
internal static class ClassicPageRules
{
    internal const string FileRefField = "FileRef";
    internal const string FileLeafRefField = "FileLeafRef";
    internal const string HtmlFileTypeField = "HTML_x0020_File_x0020_Type";
    internal const string FileTypeField = "File_x0020_Type";
    internal const string ContentTypeIdField = "ContentTypeId";
    internal const string WikiField = "WikiField";
    internal const string ModifiedField = "Modified";
    internal const string ModifiedByField = "Editor";
    internal const string CreatedField = "Created";
    internal const string ClientSideApplicationIdField = "ClientSideApplicationId";
    internal const string TitleField = "Title";
    internal const string BSNField = "BSN";
    internal const string ModernPage = PageScanComponent.ModernPage;
    internal const string WebPartPage = PageScanComponent.WebPartPage;
    internal const string WikiPage = PageScanComponent.WikiPage;
    internal const string ASPXPage = PageScanComponent.ASPXPage;
    internal const string PublishingPage = PageScanComponent.PublishingPage;
    internal const string BlogPage = PageScanComponent.BlogPage;
    internal const string DelveBlogPage = PageScanComponent.DelveBlogPage;
    internal const string DelveBlogFileType = "pointpub";

    /// <summary>
    /// Tests the captured content-type identity with the existing publishing-page policy.
    /// </summary>
    internal static bool IsPublishingContentType(string contentTypeId) =>
        SharePointLiveAspxDiscoveryProvider.IsPublishingPageContentType(contentTypeId);

    /// <summary>
    /// Applies existing pure page-type rules to captured item field values.
    /// </summary>
    internal static string GetPageType(IDictionary<string, object> fields) => PageScanComponent.GetPageType(fields);

    /// <summary>
    /// Validates the discovered physical locator against returned item metadata without inventing a URL.
    /// </summary>
    internal static string ResolvePhysicalPageUrl(IDictionary<string, object> fields, string discoveredUrl) =>
        PageScanComponent.ResolvePhysicalPageUrl(fields, discoveredUrl);
}