#nullable enable
using Microsoft.SharePoint.Client;
using Microsoft.SharePoint.Client.WebParts;
using PnP.Scanning.Core.Pipeline.Contracts;
using PnP.Scanning.Core.Scanners;

namespace PnP.Scanning.Core.Pipeline.Collection;

internal static class PageWebPartSourceReader
{
    internal static async Task<(Dictionary<string, SourceField> Properties, ClassicPageWebPartSource[] Parts)> ReadAsync(
        ClientContext context, string pageUrl, string pageType, Dictionary<string, SourceField> fields, CancellationToken token, bool includeTitleBar = false)
    {
        token.ThrowIfCancellationRequested();
        var file = context.Web.GetFileByServerRelativeUrl(pageUrl);
        var manager = file.GetLimitedWebPartManager(PersonalizationScope.Shared);
        var properties = new Dictionary<string, SourceField>();
        var definitions = new List<(string? ControlId, WebPartDefinition Definition)>();
        if (pageType == ClassicPageRules.WikiPage)
        {
            var placeholders = WikiContentParser.Parse(fields.GetValueOrDefault("WikiField")?.Text).Placeholders;
            if (placeholders.Count == 0) return (properties, []);
            context.Load(manager);
            foreach (var placeholder in placeholders)
            {
                var scope = new ExceptionHandlingScope(context);
                using (scope.StartScope())
                {
                    using (scope.StartTry())
                    {
                        var part = manager.WebParts.GetByControlId(placeholder.ControlId);
                        context.Load(part, x => x.Id, x => x.WebPart.ExportMode, x => x.WebPart.Title, x => x.WebPart.ZoneIndex,
                            x => x.WebPart.IsClosed, x => x.WebPart.Hidden, x => x.WebPart.Properties);
                        definitions.Add((placeholder.ControlId, part));
                    }
                    using (scope.StartCatch()) { }
                }
            }
            await context.ExecuteQueryAsync();
        }
        else
        {
            if (pageType == ClassicPageRules.WebPartPage) context.Load(file.Properties);
            if (pageType == ClassicPageRules.PublishingPage) context.Load(file.ListItemAllFields);
            context.Load(manager);
            var parts = context.LoadQuery(manager.WebParts.IncludeWithDefaultProperties(
                x => x.Id, x => x.ZoneId, x => x.WebPart.ExportMode, x => x.WebPart.Title, x => x.WebPart.ZoneIndex,
                x => x.WebPart.IsClosed, x => x.WebPart.Hidden, x => x.WebPart.Properties));
            await context.ExecuteQueryAsync();
            definitions.AddRange(parts.Select(x => ((string?)null, x)));
            if (pageType == ClassicPageRules.WebPartPage) properties = ClassicPageOnlineSource.Fields(file.Properties.FieldValues);
            if (pageType == ClassicPageRules.PublishingPage && file.ListItemAllFields.FieldValues.TryGetValue("PublishingPageLayout", out var layout))
            {
                // Retain the original URL field, including its friendly description.
                fields["PublishingPageLayout"] = ClassicPageOnlineSource.Fields(new Dictionary<string, object> { ["PublishingPageLayout"] = layout })["PublishingPageLayout"];
            }
        }
        token.ThrowIfCancellationRequested();
        var exports = new Dictionary<Guid, ClientResult<string>>();
        foreach (var (_, part) in definitions.Where(x => x.Definition.ServerObjectIsNull == false))
        {
            if (part.WebPart.ExportMode == WebPartExportMode.All &&
                !(pageType == ClassicPageRules.WebPartPage && !includeTitleBar && part.ZoneId.Equals("TitleBar", StringComparison.OrdinalIgnoreCase)))
                exports[part.Id] = manager.ExportWebPart(part.Id);
        }
        if (exports.Count > 0) await context.ExecuteQueryAsync();
        token.ThrowIfCancellationRequested();
        var output = definitions.Where(x => x.Definition.ServerObjectIsNull == false).Select(x =>
        {
            exports.TryGetValue(x.Definition.Id, out var xml);
            return new ClassicPageWebPartSource(x.Definition.Id, x.ControlId, x.ControlId == null ? x.Definition.ZoneId : "",
                x.Definition.WebPart.ZoneIndex, x.Definition.WebPart.Title, x.Definition.WebPart.Hidden, x.Definition.WebPart.IsClosed,
                x.Definition.WebPart.ExportMode.ToString(), ClassicPageOnlineSource.Fields(x.Definition.WebPart.Properties.FieldValues),
                xml?.Value, xml == null ? SourceReadState.NotAttempted : SourceReadState.Complete);
        }).ToArray();
        return (properties, output);
    }
}
