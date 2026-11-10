using PnP.Scanning.Core.Pipeline.Analysis.Page;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Pipeline.Contracts.Page;
using PnP.Scanning.Core.Scanners.WebPartMapping;

namespace PnP.Scanning.Core.Scanners;

// Share the existing pure layout/type helpers without invoking the online extraction methods.
/// <summary>
/// Shares page layout and web-part interpretation between existing and detached analysis.
/// </summary>
internal static partial class PageWebPartExtractor
{
    /// <summary>
    /// Builds ordered detached web-part rows from captured page fields, definitions, and original exports.
    /// </summary>
    internal static List<ClassicPageWebPartRow> Build(
        ClassicPageRow page,
        FilePageEvidence source,
        bool exportProperties,
        bool includeTitleBar = false)
    {
        string wiki = source.Fields.GetValueOrDefault("WikiField")?.Text;
        List<WebPartEntity> entities = new List<WebPartEntity>();
        PageLayout layout = PageLayout.WebPart_Custom;
        if (page.PageType == ClassicPageRules.WikiPage)
        {
            WikiContentParseResult parsed = WikiContentParser.Parse(wiki);
            page.Layout = PageLayoutDetector.ToLayoutString(PageLayoutDetector.DetectWikiLayout(wiki));
            entities.AddRange(parsed.TextParts);
            entities.AddRange(parsed.MediaParts);
            foreach (WikiWebPartPlaceholder placeholder in parsed.Placeholders)
            {
                ClassicPageWebPartSource part =
                    source.WebParts.FirstOrDefault(x => x.ControlId == placeholder.ControlId);
                if (part == null) continue;
                WebPartEntity entity = Entity(part);
                entity.ServerControlId = placeholder.Id;
                entity.Row = placeholder.Row;
                entity.Column = placeholder.Column;
                entity.Order = placeholder.Order;
                entity.ZoneId = "";
                entities.Add(entity);
            }

            entities = entities.OrderBy(x => x.Row).ThenBy(x => x.Column).ThenBy(x => x.Order).ToList();
        }
        else
        {
            if (page.PageType == ClassicPageRules.WebPartPage || source.FileLevelOnly)
            {
                layout = PageLayoutDetector.DetectWebPartPageLayout(
                    source.FileProperties.GetValueOrDefault("vti_setuppath")?.Text);
                page.Layout = PageLayoutDetector.ToLayoutString(layout);
            }
            else
            {
                page.Layout = SnapshotPageLayout.GetPublishingPageLayoutName(source.Fields);
            }

            ClassicPageWebPartSource[] parts = page.PageType == ClassicPageRules.PublishingPage
                ? source.WebParts.OrderBy(x => x.ZoneIndex).ToArray()
                : source.WebParts;
            foreach (ClassicPageWebPartSource part in parts)
            {
                if ((page.PageType == ClassicPageRules.WebPartPage || source.FileLevelOnly) && !includeTitleBar &&
                    part.ZoneId.Equals("TitleBar", StringComparison.OrdinalIgnoreCase))
                    continue;
                WebPartEntity entity = Entity(part);
                if (page.PageType == ClassicPageRules.WebPartPage || source.FileLevelOnly)
                {
                    entity.Row = GetRow(part.ZoneId, layout);
                    entity.Column = GetColumn(part.ZoneId, layout);
                }

                entities.Add(entity);
            }
        }

        return ToRows(Storage.Pipeline.ClassicCompatibility.ToLegacyPage(page), entities, exportProperties)
            .Select(CollectionJson.Convert<ClassicPageWebPartRow>)
            .ToList();
    }

    /// <summary>
    /// Restores a detached part entity and resolves its type from captured XML or properties.
    /// </summary>
    private static WebPartEntity Entity(ClassicPageWebPartSource part)
    {
        Dictionary<string, object> properties = part.Properties.ToDictionary(x => x.Key, x => x.Value.ToValue());
        return new WebPartEntity
        {
            Id = part.Id,
            ServerControlId = part.Id.ToString(),
            Title = part.Title,
            Type = part.ExportState.Succeeded ? GetTypeFromXml(part.ExportXml) : GetTypeFromProperties(properties),
            ZoneId = part.ZoneId,
            ZoneIndex = (uint)part.ZoneIndex,
            Order = part.ZoneIndex,
            Hidden = part.Hidden,
            IsClosed = part.IsClosed,
            Properties = part.Properties.ToDictionary(
                x => x.Key,
                x => x.Value.Text ?? "",
                StringComparer.InvariantCultureIgnoreCase),
        };
    }
}