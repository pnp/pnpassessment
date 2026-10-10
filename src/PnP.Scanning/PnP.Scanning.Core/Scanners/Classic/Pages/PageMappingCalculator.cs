using PnP.Scanning.Core.Scanners.WebPartMapping;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Pipeline.Contracts;
namespace PnP.Scanning.Core.Scanners;

internal static class PageMappingCalculator
{
    internal static void ApplyMapping(ClassicPage page, IReadOnlyList<ClassicPageWebPart> webParts, WebPartMappingManager mapping)
    {
        ArgumentNullException.ThrowIfNull(page); ArgumentNullException.ThrowIfNull(mapping);
        var projected = ClassicPageSourceJson.Convert<ClassicPageRow>(page);
        var parts = webParts?.Select(ClassicPageSourceJson.Convert<ClassicPageWebPartRow>).ToArray() ?? [];
        SnapshotPageMappingCalculator.ApplyMapping(projected, parts, mapping);
        page.WebPartCount = projected.WebPartCount; page.MappingPercentage = projected.MappingPercentage; page.UnmappedWebParts = projected.UnmappedWebParts;
        for (var i = 0; i < parts.Length; i++) webParts[i].IsMappable = parts[i].IsMappable;
    }
}
