#nullable enable
using System.Text.Json;
using PnP.Scanning.Core.Pipeline.Contracts.Page;

namespace PnP.Scanning.Core.Pipeline.Analysis.Page;

internal sealed record PhysicalFileAnalysis(
    Guid SiteCollectionId, Guid FileUniqueId, Guid? WebId, Guid? ListId, int? ListItemId,
    AspxPageClassification? Classification, string? HandlerResolution, string? SourceSha256);

internal static class PhysicalFileAnalysisRules
{
    internal static PhysicalFileAnalysis Analyze(SPFileSource file)
    {
        AspxPageEvidence? evidence = file.AspxEvidenceJson == null ? null :
            JsonSerializer.Deserialize<AspxPageEvidence>(file.AspxEvidenceJson)
                ?? throw new InvalidDataException("ASPX evidence must be an object.");
        var source = evidence?.OriginalBytes == null ? null : AspxSourceParser.Parse(evidence.OriginalBytes);
        var classification = file.ServerRelativeUrl?.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase) == true
            ? AspxPageClassifier.Classify(source?.Handler, evidence?.ListTemplate, evidence?.IsListView,
                evidence?.IsListForm, evidence?.IsCatalog, WebRelativePath(file)) : null;
        return new(file.SiteCollectionId, file.FileUniqueId, file.WebId, file.ListId, file.ListItemId,
            classification, source?.Resolution, source?.Sha256);
    }

    private static string? WebRelativePath(SPFileSource file)
    {
        if (file.SiteUrl == null || file.WebUrl == null || file.ServerRelativeUrl == null) return null;
        var locator = file.SiteUrl.TrimEnd('/') + (file.WebUrl == "/" ? "" : file.WebUrl);
        if (!Uri.TryCreate(locator, UriKind.Absolute, out var web)) return null;
        var prefix = Uri.UnescapeDataString(web.AbsolutePath).TrimEnd('/') + "/";
        return file.ServerRelativeUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? file.ServerRelativeUrl[prefix.Length..] : null;
    }
}
