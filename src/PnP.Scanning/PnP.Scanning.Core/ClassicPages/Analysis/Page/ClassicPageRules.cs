#nullable enable
using System.Globalization;

namespace PnP.Scanning.Core.ClassicPages.Analysis.Page;

/// <summary>
/// Owns ASPX item-field readiness classification and physical-locator validation.
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
    internal const string ModernPage = "ModernPage";
    internal const string WebPartPage = "WebPartPage";
    internal const string WikiPage = "WikiPage";
    internal const string ASPXPage = "ASPXPage";
    internal const string PublishingPage = "PublishingPage";
    internal const string BlogPage = "BlogPage";
    internal const string DelveBlogPage = "DelveBlogPage";
    internal static readonly Guid PublishingSiteFeature = new("F6924D36-2FA8-4F0B-B16D-06B7250180FA");
    internal static readonly Guid PublishingWebFeature = new("94C94CA6-B32F-4DA9-A9E3-1F3D343D7ECB");
    private static readonly Guid ModernPageFeature = new("B6917CB1-93A0-4B97-A84D-7CF49975D4EC");

    /// <summary>Admits only physically observed ASPX filenames or paths, without consulting library or item classification.</summary>
    internal static bool IsAspxFile(string? name, string? path) =>
        name?.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase) == true ||
        path?.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Tests the captured content-type identity with the existing publishing-page policy.
    /// </summary>
    internal static bool IsPublishingContentType(string? contentTypeId) =>
        !string.IsNullOrWhiteSpace(contentTypeId) && (contentTypeId.StartsWith(
            "0x010100C568DB52D9D0A14D9B2FDCC96666E9F2007948130EC3DB064584E219954237AF39",
            StringComparison.OrdinalIgnoreCase) || contentTypeId.StartsWith(
            "0x01010007FF3E057FA8AB4AA42FCB67B453FFC1",
            StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Applies existing pure page-type rules to captured item field values.
    /// </summary>
    internal static string GetPageType(IDictionary<string, object?> fields)
    {
        if (ReadText(fields, HtmlFileTypeField, "") == "SharePoint.WebPartPage.Document") return WebPartPage;
        if (Guid.TryParse(ReadText(fields, ClientSideApplicationIdField, ""), out Guid application) &&
            application == ModernPageFeature)
            return ModernPage;
        if (ReadText(fields, WikiField) != null) return WikiPage;
        return ReadText(fields, BSNField) != "" ? ASPXPage : WikiPage;
    }

    /// <summary>
    /// Validates the discovered physical locator against returned item metadata without inventing a URL.
    /// </summary>
    internal static string ResolvePhysicalPageUrl(IDictionary<string, object?> fields, string discoveredUrl)
    {
        if (string.IsNullOrWhiteSpace(discoveredUrl) || !discoveredUrl.StartsWith('/'))
            throw new InvalidDataException(
                "Physical page assessment requires its discovered server-relative URL; ListItemId is not a URL.");
        string? fileRef = ReadText(fields, FileRefField, "");
        if (!string.IsNullOrWhiteSpace(fileRef) && !string.Equals(
                fileRef,
                discoveredUrl,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "The list item's FileRef differs from the discovered file; its owner/identity must be resolved before assessment.");
        return discoveredUrl;
    }

    /// <summary>
    /// Preserves the legacy invariant scalar conversion and its distinct missing-field defaults.
    /// </summary>
    private static string? ReadText(IDictionary<string, object?> fields, string name, string? defaultValue = null) =>
        fields.TryGetValue(name, out object? value) && value != null
            ? Convert.ToString(value, CultureInfo.InvariantCulture)
            : defaultValue;
}