using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Discovery;
#nullable enable
using Microsoft.SharePoint.Client;
using Microsoft.SharePoint.Client.WebParts;
using PnP.Scanning.Core.Pipeline.Analysis.Page;
using PnP.Scanning.Core.Pipeline.Collection.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Page;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;

namespace PnP.Scanning.Core.Pipeline.Collection.Page;

/// <summary>
/// Captures CSOM web-part definitions, properties, and original export XML for offline analysis.
/// </summary>
internal static class PageWebPartSourceReader
{
    /// <summary>
    /// Captures page-specific CSOM web parts, original export XML, properties, and explicit read outcomes.
    /// </summary>
    internal static async Task<(Dictionary<string, SourceField> Properties, ClassicPageWebPartSource[] Parts)>
        ReadAsync(
            ClientContext context,
            string pageUrl,
            string pageType,
            Dictionary<string, SourceField> fields,
            CancellationToken token,
            bool includeTitleBar = false)
    {
        token.ThrowIfCancellationRequested();
        Dictionary<string, SourceField>? properties = new Dictionary<string, SourceField>();
        List<(string? ControlId, WebPartDefinition Definition)>? definitions =
            new List<(string? ControlId, WebPartDefinition Definition)>();
        List<WikiWebPartPlaceholder>? placeholders = pageType == ClassicPageRules.WikiPage
            ? WikiContentParser.Parse(fields.GetValueOrDefault("WikiField")?.Text).Placeholders
            : null;
        if (placeholders?.Count == 0) return (properties, []);
        Microsoft.SharePoint.Client.File? file = context.Web.GetFileByServerRelativeUrl(pageUrl);
        LimitedWebPartManager? manager = file.GetLimitedWebPartManager(PersonalizationScope.Shared);
        if (pageType == ClassicPageRules.WikiPage)
        {
            context.Load(manager);
            foreach (WikiWebPartPlaceholder? placeholder in placeholders!)
            {
                ExceptionHandlingScope? scope = new ExceptionHandlingScope(context);
                using (scope.StartScope())
                {
                    using (scope.StartTry())
                    {
                        WebPartDefinition? part = manager.WebParts.GetByControlId(placeholder.ControlId);
                        context.Load(
                            part,
                            x => x.Id,
                            x => x.WebPart.ExportMode,
                            x => x.WebPart.Title,
                            x => x.WebPart.ZoneIndex,
                            x => x.WebPart.IsClosed,
                            x => x.WebPart.Hidden,
                            x => x.WebPart.Properties);
                        definitions.Add((placeholder.ControlId, part));
                    }

                    using (scope.StartCatch())
                    {
                    }
                }
            }

            await context.ExecuteQueryAsync();
        }
        else
        {
            if (pageType == ClassicPageRules.WebPartPage) context.Load(file.Properties);
            if (pageType == ClassicPageRules.PublishingPage) context.Load(file.ListItemAllFields);
            context.Load(manager);
            IEnumerable<WebPartDefinition>? parts = context.LoadQuery(
                manager.WebParts.IncludeWithDefaultProperties(
                    x => x.Id,
                    x => x.ZoneId,
                    x => x.WebPart.ExportMode,
                    x => x.WebPart.Title,
                    x => x.WebPart.ZoneIndex,
                    x => x.WebPart.IsClosed,
                    x => x.WebPart.Hidden,
                    x => x.WebPart.Properties));
            await context.ExecuteQueryAsync();
            definitions.AddRange(parts.Select(x => ((string?)null, x)));
            if (pageType == ClassicPageRules.WebPartPage)
                properties = ClassicPageOnlineSource.Fields(file.Properties.FieldValues);
            if (pageType == ClassicPageRules.PublishingPage &&
                file.ListItemAllFields.FieldValues.TryGetValue("PublishingPageLayout", out object? layout))
            {
                // Retain the original URL field, including its friendly description.
                fields["PublishingPageLayout"] =
                    ClassicPageOnlineSource.Fields(new Dictionary<string, object> { ["PublishingPageLayout"] = layout })
                        ["PublishingPageLayout"];
            }
        }

        token.ThrowIfCancellationRequested();
        Dictionary<Guid, ClientResult<string>>? exports = new Dictionary<Guid, ClientResult<string>>();
        foreach ((string? _, WebPartDefinition? part) in
                 definitions.Where(x => x.Definition.ServerObjectIsNull == false))
        {
            if (part.WebPart.ExportMode == WebPartExportMode.All && !(pageType == ClassicPageRules.WebPartPage &&
                                                                      !includeTitleBar &&
                                                                      part.ZoneId.Equals(
                                                                          "TitleBar",
                                                                          StringComparison.OrdinalIgnoreCase)))
                exports[part.Id] = manager.ExportWebPart(part.Id);
        }

        if (exports.Count > 0) await context.ExecuteQueryAsync();
        token.ThrowIfCancellationRequested();
        ClassicPageWebPartSource[]? output = definitions.Where(x => x.Definition.ServerObjectIsNull == false)
            .Select(x =>
            {
                exports.TryGetValue(x.Definition.Id, out ClientResult<string>? xml);
                return new ClassicPageWebPartSource(
                    x.Definition.Id,
                    x.ControlId,
                    x.ControlId == null ? x.Definition.ZoneId : "",
                    x.Definition.WebPart.ZoneIndex,
                    x.Definition.WebPart.Title,
                    x.Definition.WebPart.Hidden,
                    x.Definition.WebPart.IsClosed,
                    x.Definition.WebPart.ExportMode.ToString(),
                    ClassicPageOnlineSource.Fields(x.Definition.WebPart.Properties.FieldValues),
                    xml?.Value,
                    xml == null ? SourceReadState.NotAttempted : SourceReadState.Complete);
            })
            .ToArray();
        return (properties, output);
    }
}