using PnP.Scanning.Core.ClassicPages.Contracts.Shared;


#nullable enable


namespace PnP.Scanning.Core.ClassicPages.Analysis.Page;

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