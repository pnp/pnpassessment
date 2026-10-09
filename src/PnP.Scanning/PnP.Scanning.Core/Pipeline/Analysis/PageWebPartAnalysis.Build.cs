using PnP.Scanning.Core.Scanners;
using ClassicPage = PnP.Scanning.Core.Pipeline.Contracts.ClassicPageRow;
using ClassicPageWebPart = PnP.Scanning.Core.Pipeline.Contracts.ClassicPageWebPartRow;
using PnP.Scanning.Core.Pipeline.Contracts;

using PnP.Scanning.Core.Scanners.WebPartMapping;

namespace PnP.Scanning.Core.Pipeline.Analysis;

internal static partial class PageWebPartAnalysis
{
    internal static List<ClassicPageWebPart> Build(ClassicPage page, ClassicPageItemSource source, bool exportProperties, bool includeTitleBar = false)
    {
        var wiki = source.Fields.GetValueOrDefault("WikiField")?.Text;
        var entities = new List<WebPartEntity>();
        var layout = PageLayout.WebPart_Custom;
        if (page.PageType == ClassicPageRules.WikiPage)
        {
            var parsed = WikiContentParser.Parse(wiki);
            page.Layout = PageLayoutDetector.ToLayoutString(PageLayoutDetector.DetectWikiLayout(wiki));
            entities.AddRange(parsed.TextParts);
            entities.AddRange(parsed.MediaParts);
            foreach (var placeholder in parsed.Placeholders)
            {
                var part = source.WebParts.FirstOrDefault(x => x.ControlId == placeholder.ControlId);
                if (part == null) continue;
                var entity = Entity(part);
                entity.ServerControlId = placeholder.Id;
                entity.Row = placeholder.Row; entity.Column = placeholder.Column; entity.Order = placeholder.Order;
                entity.ZoneId = "";
                entities.Add(entity);
            }
            entities = entities.OrderBy(x => x.Row).ThenBy(x => x.Column).ThenBy(x => x.Order).ToList();
        }
        else
        {
            if (page.PageType == ClassicPageRules.WebPartPage)
            {
                layout = PageLayoutDetector.DetectWebPartPageLayout(source.FileProperties.GetValueOrDefault("vti_setuppath")?.Text);
                page.Layout = PageLayoutDetector.ToLayoutString(layout);
            }
            else
            {
                var field = source.Fields.GetValueOrDefault("PublishingPageLayout");
                page.Layout = field?.Type == "Url" && field.Value.TryGetProperty("Description", out var description)
                    ? description.GetString() ?? "" : "";
            }
            var parts = page.PageType == ClassicPageRules.PublishingPage
                ? source.WebParts.OrderBy(x => x.ZoneIndex).ToArray() : source.WebParts;
            foreach (var part in parts)
            {
                if (page.PageType == ClassicPageRules.WebPartPage && !includeTitleBar && part.ZoneId.Equals("TitleBar", StringComparison.OrdinalIgnoreCase)) continue;
                var entity = Entity(part);
                if (page.PageType == ClassicPageRules.WebPartPage)
                { entity.Row = GetRow(part.ZoneId, layout); entity.Column = GetColumn(part.ZoneId, layout); }
                entities.Add(entity);
            }
        }
        return ToRows(page, entities, exportProperties);
    }

    private static WebPartEntity Entity(ClassicPageWebPartSource part)
    {
        var properties = part.Properties.ToDictionary(x => x.Key, x => x.Value.ToValue());
        return new WebPartEntity
        {
            Id = part.Id, ServerControlId = part.Id.ToString(), Title = part.Title,
            Type = part.ExportState.Succeeded ? GetTypeFromXml(part.ExportXml) : GetTypeFromProperties(properties),
            ZoneId = part.ZoneId, ZoneIndex = (uint)part.ZoneIndex, Order = part.ZoneIndex,
            Hidden = part.Hidden, IsClosed = part.IsClosed,
            Properties = part.Properties.ToDictionary(x => x.Key, x => x.Value.Text ?? "", StringComparer.InvariantCultureIgnoreCase),
        };
    }
}
