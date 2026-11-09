#nullable enable
using PnP.Scanning.Core.Pipeline.Contracts.Page;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Scanners.WebPartMapping;
using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.Pipeline.Analysis.Page;

internal static class SPFileFactConsolidator
{
    internal static SPFileSource Consolidate(SPFileSource? previous, SPFileSource incoming) =>
        ClassicPageSourceJson.Convert<SPFileSource>(SPFileWriter.Consolidate(
            previous == null ? null : ClassicPageSourceJson.Convert<SPFile>(previous),
            ClassicPageSourceJson.Convert<SPFile>(incoming)));
}

internal static class SnapshotPageMappingCalculator
{
    internal static void ApplyMapping(ClassicPageRow page, IReadOnlyList<ClassicPageWebPartRow> parts,
        WebPartMappingManager mapping)
    {
        var legacyPage = ClassicPageSourceJson.Convert<ClassicPage>(page);
        var legacyParts = parts.Select(ClassicPageSourceJson.Convert<ClassicPageWebPart>).ToArray();
        PageMappingCalculator.ApplyMapping(legacyPage, legacyParts, mapping);
        page.WebPartCount = legacyPage.WebPartCount;
        page.MappingPercentage = legacyPage.MappingPercentage;
        page.UnmappedWebParts = legacyPage.UnmappedWebParts;
        for (var index = 0; index < parts.Count; index++) parts[index].IsMappable = legacyParts[index].IsMappable;
    }
}

internal static class SnapshotPageLayout
{
    internal static string? GetPublishingPageLayoutUrl(Dictionary<string, SourceField> fields) =>
        fields.TryGetValue("PublishingPageLayout", out var value) && value.Type == "Url" &&
        value.Value.TryGetProperty("Url", out var url) ? url.GetString() : null;

    internal static string GetPublishingPageLayoutName(Dictionary<string, SourceField> fields) =>
        fields.TryGetValue("PublishingPageLayout", out var value) && value.Type == "Url" &&
        value.Value.TryGetProperty("Description", out var description) ? description.GetString() ?? "" : "";
}

internal static class SnapshotHomePage
{
    internal static bool? ResolveHomePageState(string? url, string? welcome, bool readSucceeded, bool? observed) =>
        readSucceeded ? HomePageDetector.IsHomePage(url, welcome) : observed;
}
