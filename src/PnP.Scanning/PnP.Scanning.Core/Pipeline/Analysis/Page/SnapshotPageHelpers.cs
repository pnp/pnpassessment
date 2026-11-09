#nullable enable
using PnP.Scanning.Core.Pipeline.Contracts.Page;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Scanners.WebPartMapping;
using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.Pipeline.Analysis.Page;

/// <summary>
/// Reuses native-file evidence consolidation for detached snapshot observations.
/// </summary>
internal static class SPFileFactConsolidator
{
    /// <summary>
    /// Merges detached observations through the existing native-file evidence policy.
    /// </summary>
    internal static SPFileSource Consolidate(SPFileSource? previous, SPFileSource incoming) =>
        ClassicPageSourceJson.Convert<SPFileSource>(
            SPFileWriter.Consolidate(
                previous == null ? null : ClassicPageSourceJson.Convert<SPFile>(previous),
                ClassicPageSourceJson.Convert<SPFile>(incoming)));
}

/// <summary>
/// Applies existing web-part readiness rules to detached page and part results.
/// </summary>
internal static class SnapshotPageMappingCalculator
{
    /// <summary>
    /// Applies existing part mapping and copies readiness totals back to detached page results.
    /// </summary>
    internal static void ApplyMapping(
        ClassicPageRow page,
        IReadOnlyList<ClassicPageWebPartRow> parts,
        WebPartMappingManager mapping)
    {
        ClassicPage? legacyPage = ClassicPageSourceJson.Convert<ClassicPage>(page);
        ClassicPageWebPart[]? legacyParts = parts.Select(ClassicPageSourceJson.Convert<ClassicPageWebPart>).ToArray();
        PageMappingCalculator.ApplyMapping(legacyPage, legacyParts, mapping);
        page.WebPartCount = legacyPage.WebPartCount;
        page.MappingPercentage = legacyPage.MappingPercentage;
        page.UnmappedWebParts = legacyPage.UnmappedWebParts;
        for (int index = 0; index < parts.Count; index++) parts[index].IsMappable = legacyParts[index].IsMappable;
    }
}

/// <summary>
/// Reads publishing layout declarations from captured typed fields.
/// </summary>
internal static class SnapshotPageLayout
{
    /// <summary>
    /// Reads the captured publishing-layout URL without resolving network content.
    /// </summary>
    internal static string? GetPublishingPageLayoutUrl(Dictionary<string, SourceField> fields) =>
        fields.TryGetValue("PublishingPageLayout", out SourceField? value) && value.Type == "Url" &&
        value.Value.TryGetProperty("Url", out System.Text.Json.JsonElement url)
            ? url.GetString()
            : null;

    /// <summary>
    /// Reads the captured publishing-layout description while preserving an absent declaration.
    /// </summary>
    internal static string GetPublishingPageLayoutName(Dictionary<string, SourceField> fields) =>
        fields.TryGetValue("PublishingPageLayout", out SourceField? value) && value.Type == "Url" &&
        value.Value.TryGetProperty("Description", out System.Text.Json.JsonElement description)
            ? description.GetString() ?? ""
            : "";
}

/// <summary>
/// Resolves home-page identity without treating unavailable welcome-page evidence as false.
/// </summary>
internal static class SnapshotHomePage
{
    /// <summary>
    /// Uses a successful welcome-page read or retains the previously observed nullable home-page state.
    /// </summary>
    internal static bool? ResolveHomePageState(string? url, string? welcome, bool readSucceeded, bool? observed) =>
        readSucceeded ? HomePageDetector.IsHomePage(url, welcome) : observed;
}