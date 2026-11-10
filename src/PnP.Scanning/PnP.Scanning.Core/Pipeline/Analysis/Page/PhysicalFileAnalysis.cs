#nullable enable
using System.Text.Json;
using PnP.Scanning.Core.Pipeline.Contracts.Page;

namespace PnP.Scanning.Core.Pipeline.Analysis.Page;

/// <summary>
/// Binds native file identity to an evidence-based ASPX classification and source digest.
/// </summary>
internal sealed record PhysicalFileAnalysis(
    Guid SiteCollectionId,
    Guid FileUniqueId,
    Guid? WebId,
    Guid? ListId,
    int? ListItemId,
    AspxPageClassification? Classification,
    string? HandlerResolution,
    string? SourceSha256);

/// <summary>
/// Classifies physical files independently of item-backed page admission.
/// </summary>
internal static class PhysicalFileAnalysisRules
{
    /// <summary>
    /// Classifies acquired ASPX bytes independently of file admission and preserves native identity.
    /// </summary>
    internal static PhysicalFileAnalysis Analyze(PhysicalFileFacts file)
    {
        AspxPageEvidence? evidence = file.AspxEvidenceJson == null
            ? null
            : JsonSerializer.Deserialize<AspxPageEvidence>(file.AspxEvidenceJson) ??
              throw new InvalidDataException("ASPX evidence must be an object.");
        AspxSourceFacts? source =
            evidence?.OriginalBytes == null ? null : AspxSourceParser.Parse(evidence.OriginalBytes);
        AspxPageClassification? classification =
            file.ServerRelativeUrl?.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase) == true
                ? AspxPageClassifier.Classify(
                    source?.Handler,
                    evidence?.ListTemplate,
                    evidence?.IsListView,
                    evidence?.IsListForm,
                    evidence?.IsCatalog,
                    WebRelativePath(file))
                : null;
        return new(
            file.SiteCollectionId,
            file.FileUniqueId,
            file.WebId,
            file.ListId,
            file.ListItemId,
            classification,
            source?.Resolution,
            source?.Sha256);
    }

    /// <summary>
    /// Returns a captured file path relative to its owning Web when that relationship is known.
    /// </summary>
    private static string? WebRelativePath(PhysicalFileFacts file)
    {
        if (file.SiteUrl == null || file.WebUrl == null || file.ServerRelativeUrl == null) return null;
        string? locator = file.SiteUrl.TrimEnd('/') + (file.WebUrl == "/" ? "" : file.WebUrl);
        if (!Uri.TryCreate(locator, UriKind.Absolute, out Uri? web)) return null;
        string? prefix = Uri.UnescapeDataString(web.AbsolutePath).TrimEnd('/') + "/";
        return file.ServerRelativeUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? file.ServerRelativeUrl[prefix.Length..]
            : null;
    }
}