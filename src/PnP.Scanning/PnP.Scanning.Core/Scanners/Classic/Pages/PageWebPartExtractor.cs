using Microsoft.SharePoint.Client;
using PnP.Scanning.Core.Pipeline.Collection;
using PnP.Scanning.Core.Pipeline.Contracts;
using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.Scanners;

/// <summary>Compatibility facade. The reader acquires SDK values; the builder performs pure analysis.</summary>
internal static class PageWebPartExtractor
{
    private const string PublishingPageLayoutField = "PublishingPageLayout";
    internal static Task<List<ClassicPageWebPart>> ExtractFromWebPartPageAsync(ClientContext context, ClassicPage page,
        bool exportWebPartProperties, bool includeTitleBarWebPart = false) =>
        ExtractAsync(context, page, ClassicPageRules.WebPartPage, null, exportWebPartProperties, includeTitleBarWebPart);
    internal static Task<List<ClassicPageWebPart>> ExtractFromWikiPageAsync(ClientContext context, ClassicPage page, string wikiFieldHtml,
        bool exportWebPartProperties) => ExtractAsync(context, page, ClassicPageRules.WikiPage, wikiFieldHtml, exportWebPartProperties);
    internal static Task<List<ClassicPageWebPart>> ExtractFromPublishingPageAsync(ClientContext context, ClassicPage page,
        bool exportWebPartProperties) => ExtractAsync(context, page, ClassicPageRules.PublishingPage, null, exportWebPartProperties);
    private static async Task<List<ClassicPageWebPart>> ExtractAsync(ClientContext context, ClassicPage page, string type, string wiki,
        bool export, bool includeTitleBar = false)
    {
        var fields = new Dictionary<string, SourceField>();
        if (wiki != null) fields["WikiField"] = SourceField.Of("String", wiki);
        var (properties, parts) = await PageWebPartSourceReader.ReadAsync(context, page.PageUrl, type, fields, CancellationToken.None, includeTitleBar);
        var input = new ClassicPageItemSource(page.SiteUrl, page.WebUrl, "", page.ListId, page.ListTitle, page.ListUrl,
            fields, properties, parts, SourceReadState.Complete, SourceReadState.Complete, null, SourceReadState.NotAttempted);
        page.PageType = type;
        var projection = ClassicPageSourceJson.Convert<ClassicPageRow>(page);
        var result = PageWebPartAnalysis.Build(projection, input, export, includeTitleBar);
        page.Layout = projection.Layout;
        return result.Select(ClassicPageSourceJson.Convert<ClassicPageWebPart>).ToList();
    }
    internal static string GetTypeFromXml(string xml) => PageWebPartAnalysis.GetTypeFromXml(xml);
    internal static string GetTypeFromProperties(IDictionary<string, object> properties) => PageWebPartAnalysis.GetTypeFromProperties(properties);
        internal static string GetPublishingPageLayoutName(IDictionary<string, object> fieldValues)
        {
            if (fieldValues != null &&
                fieldValues.TryGetValue(PublishingPageLayoutField, out var value) &&
                value != null &&
                !string.IsNullOrEmpty(value.ToString()))
            {
                var description = (value as FieldUrlValue)?.Description;
                return string.IsNullOrEmpty(description) ? "" : description;
            }

            return "";
        }

}
