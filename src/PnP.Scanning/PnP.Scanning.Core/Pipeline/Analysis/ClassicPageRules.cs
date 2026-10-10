using System.Globalization;
namespace PnP.Scanning.Core.Pipeline.Analysis;

/// <summary>Page rules shared by the legacy facade and snapshot analysis; no online objects.</summary>
internal static class ClassicPageRules
{
    internal static bool IsPublishingContentType(string contentTypeId) => !string.IsNullOrWhiteSpace(contentTypeId) &&
        (contentTypeId.StartsWith("0x010100C568DB52D9D0A14D9B2FDCC96666E9F2007948130EC3DB064584E219954237AF39", StringComparison.OrdinalIgnoreCase) ||
         contentTypeId.StartsWith("0x01010007FF3E057FA8AB4AA42FCB67B453FFC1", StringComparison.OrdinalIgnoreCase));
    private static readonly Guid FeatureId_Web_ModernPage = new("B6917CB1-93A0-4B97-A84D-7CF49975D4EC");
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

        // Page Types
        internal const string ModernPage = "ModernPage";
        internal const string WebPartPage = "WebPartPage";
        internal const string WikiPage = "WikiPage";
        internal const string ASPXPage = "ASPXPage";
        internal const string PublishingPage = "PublishingPage";
        internal const string BlogPage = "BlogPage";
        internal const string DelveBlogPage = "DelveBlogPage";

        // File type value identifying a Delve blog page (point publishing)
        internal const string DelveBlogFileType = "pointpub";

        // The localized default home page resource (e.g. "Home") used to recognize an uncustomized STS#0 home page.
        internal const string WikiHomePageResource = "$Resources:WikiPageHomePageName";

        private static T GetFieldValue<T>(IDictionary<string, object> fieldValues, string fieldName, T defaultValue = default)
        {
            if (fieldValues.ContainsKey(fieldName) && fieldValues[fieldName] != null)
            {
                if (fieldValues[fieldName] is T typed) return typed;
                if (typeof(T) == typeof(string))
                    return (T)(object)Convert.ToString(fieldValues[fieldName], System.Globalization.CultureInfo.InvariantCulture);
                return defaultValue;
            }

            return defaultValue;
        }
        internal static string GetPageType(IDictionary<string, object> fieldValues)
        {
            if (GetFieldValue(fieldValues, HtmlFileTypeField, string.Empty) == "SharePoint.WebPartPage.Document")
            {
                return WebPartPage;
            }

            if (Guid.TryParse(GetFieldValue(fieldValues, ClientSideApplicationIdField, string.Empty), out var clientApplicationId) &&
                clientApplicationId == FeatureId_Web_ModernPage)
            {
                return ModernPage;
            }

            if (GetFieldValue<string>(fieldValues, WikiField) != null)
            {
                return WikiPage;
            }

            if (GetFieldValue(fieldValues, FileTypeField, string.Empty).Equals(DelveBlogFileType, StringComparison.InvariantCultureIgnoreCase))
            {
                return DelveBlogPage;
            }

            if (GetFieldValue<string>(fieldValues, BSNField) != "")
            {
                return ASPXPage;
            }
            else
            {
                return WikiPage;
            }
        }
        internal static string ResolvePhysicalPageUrl(IDictionary<string, object> fields, string discoveredUrl)
        {
            if (string.IsNullOrWhiteSpace(discoveredUrl) || !discoveredUrl.StartsWith('/'))
                throw new InvalidDataException("Physical page assessment requires its discovered server-relative URL; ListItemId is not a URL.");
            var fileRef = GetFieldValue(fields, FileRefField, string.Empty);
            if (!string.IsNullOrWhiteSpace(fileRef) && !string.Equals(fileRef, discoveredUrl, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The list item's FileRef differs from the discovered file; its owner/identity must be resolved before assessment.");
            return discoveredUrl;
        }
}
