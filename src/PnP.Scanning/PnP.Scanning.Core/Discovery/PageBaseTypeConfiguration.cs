using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace PnP.Scanning.Core.Discovery;

internal enum PageConfigurationKnowledge { Unknown, EffectiveOverride, VerifiedNoOverride, Conflicting, Unsupported }
internal enum PageConfigurationApplicability { Unknown, Applicable, OutOfScope, Unsupported }

/// <summary>
/// Exact physical-file applicability only. Raw scope strings are retained, including invalid inputs.
/// No directory inheritance, location processing, deployment probing or configuration engine is implied.
/// </summary>
internal sealed record PageBaseTypeFileScope(string SiteCollectionId, string WebId, string FileUniqueId, string ServerRelativePath);

internal sealed record PageBaseTypeConfigurationEvidence(string KnowledgeState, string EffectivePageBaseType,
    string Provenance, PageBaseTypeFileScope FileScope, string Reason);

internal sealed record PageBaseTypeConfigurationEvaluation(PageConfigurationKnowledge KnowledgeState,
    PageConfigurationApplicability Applicability, string EffectivePageBaseType, string Provenance,
    string Reason, IReadOnlyList<PageBaseTypeConfigurationEvidence> Evidence);

/// <summary>
/// A bounded immutable snapshot of supplied effective pages.pageBaseType evidence, read through
/// the existing assessment IConfiguration mechanism. These are operator assertions, not fetched
/// deployment configuration. Scan initialization/persistence owns freezing and restoring this snapshot.
/// </summary>
internal sealed class PageBaseTypeConfiguration
{
    internal const string ConfigurationSection = "PageInherits:PagesPageBaseTypeEvidence";
    internal const int CurrentEvidenceVersion = 1;
    internal const int MaximumEvidenceEntries = 128;
    private readonly IReadOnlyList<PageBaseTypeConfigurationEvidence> evidence;

    internal PageBaseTypeConfiguration(IEnumerable<PageBaseTypeConfigurationEvidence> values = null)
    {
        var retained = (values ?? Array.Empty<PageBaseTypeConfigurationEvidence>()).Take(MaximumEvidenceEntries + 1).ToArray();
        evidence = Array.AsReadOnly(retained.Length > MaximumEvidenceEntries
            ? new[] { Unsupported("ConfigurationEvidenceCountLimitExceeded") } : retained);
    }

    internal IReadOnlyList<PageBaseTypeConfigurationEvidence> Evidence => evidence;

    internal static PageBaseTypeConfiguration Capture(IConfiguration configuration) => new(
        configuration?.GetSection(ConfigurationSection).GetChildren().Select(section =>
            new PageBaseTypeConfigurationEvidence(section["KnowledgeState"], section["EffectivePageBaseType"],
                section["Provenance"], new(section["FileScope:SiteCollectionId"], section["FileScope:WebId"],
                    section["FileScope:FileUniqueId"], section["FileScope:ServerRelativePath"]), section["Reason"])));

    private sealed record Snapshot(int Version, PageBaseTypeConfigurationEvidence[] Evidence);
    internal string ToJson() => JsonSerializer.Serialize(new Snapshot(CurrentEvidenceVersion, evidence.ToArray()));

    internal static PageBaseTypeConfiguration FromJson(string json)
    {
        if (string.IsNullOrEmpty(json)) return new();
        if (json.Length > 1048576) return new(new[] { Unsupported("FrozenConfigurationCharacterLimitExceeded") });
        try
        {
            var snapshot = JsonSerializer.Deserialize<Snapshot>(json);
            return snapshot?.Version == CurrentEvidenceVersion && snapshot.Evidence != null
                ? new(snapshot.Evidence) : new(new[] { Unsupported("FrozenConfigurationVersionOrEvidenceUnsupported") });
        }
        catch (JsonException) { return new(new[] { Unsupported("FrozenConfigurationInvalidJson") }); }
    }

    internal PageBaseTypeConfigurationEvaluation Evaluate(AspxFileIdentity file)
    {
        PageBaseTypeConfigurationEvaluation Result(PageConfigurationKnowledge state, PageConfigurationApplicability applicability,
            string reason, string value = null, string provenance = null) => new(state, applicability, value, provenance, reason, evidence);
        if (evidence.Count == 0)
            return Result(PageConfigurationKnowledge.Unknown, PageConfigurationApplicability.Unknown,
                "DeploymentConfigurationUnknown;NoFrozenPagesPageBaseTypeEvidence");
        var applicable = new List<PageBaseTypeConfigurationEvidence>();
        foreach (var entry in evidence)
        {
            if (entry == null || !ValidScope(entry.FileScope, out var site, out var web, out var id))
                return Result(PageConfigurationKnowledge.Unsupported, PageConfigurationApplicability.Unsupported,
                    "ConfigurationFileScopeMissingOrUnsupported;" + entry?.Reason);
            if (file?.State != AspxIdentityState.Resolved || !ServerRelativePath(file.Url, out var path))
                return Result(PageConfigurationKnowledge.Unsupported, PageConfigurationApplicability.Unknown,
                    "SourceFileScopeCannotBeConfirmed");
            if (file.SiteCollectionId == site && file.WebId == web && file.FileUniqueId == id &&
                path.Equals(entry.FileScope.ServerRelativePath, StringComparison.OrdinalIgnoreCase)) applicable.Add(entry);
        }
        if (applicable.Count == 0)
            return Result(PageConfigurationKnowledge.Unknown, PageConfigurationApplicability.OutOfScope,
                "FrozenEvidenceOutOfScope;DeploymentConfigurationOtherwiseUnknown");
        if (applicable.Count > 1)
            return Result(PageConfigurationKnowledge.Conflicting, PageConfigurationApplicability.Applicable,
                "OverlappingEffectiveConfigurationEvidence;NoPrecedenceEngine");
        var selected = applicable[0];
        if (!Enum.TryParse<PageConfigurationKnowledge>(selected.KnowledgeState, false, out var knowledge) || !Enum.IsDefined(knowledge))
            return Result(PageConfigurationKnowledge.Unsupported, PageConfigurationApplicability.Applicable,
                "ConfigurationKnowledgeStateUnsupported:" + selected.KnowledgeState);
        if (knowledge == PageConfigurationKnowledge.Unknown && selected.EffectivePageBaseType == null)
            return Result(knowledge, PageConfigurationApplicability.Applicable, "DeploymentConfigurationUnknown;" + selected.Reason);
        if (knowledge is PageConfigurationKnowledge.Conflicting or PageConfigurationKnowledge.Unsupported)
            return Result(knowledge, PageConfigurationApplicability.Applicable, "SuppliedConfiguration" + knowledge + ";" + selected.Reason);
        if (string.IsNullOrWhiteSpace(selected.Provenance))
            return Result(PageConfigurationKnowledge.Unsupported, PageConfigurationApplicability.Applicable, "ConfigurationProvenanceMissing");
        if (knowledge == PageConfigurationKnowledge.VerifiedNoOverride && selected.EffectivePageBaseType == null)
            return Result(knowledge, PageConfigurationApplicability.Applicable, "SuppliedVerifiedNoOverride;" + selected.Reason, provenance: selected.Provenance);
        if (knowledge == PageConfigurationKnowledge.EffectiveOverride && !string.IsNullOrWhiteSpace(selected.EffectivePageBaseType))
            return Result(knowledge, PageConfigurationApplicability.Applicable, "SuppliedEffectivePagesPageBaseType;" + selected.Reason,
                selected.EffectivePageBaseType, selected.Provenance);
        // Empty, null, unknown-with-value and no-override-with-value are not interchangeable.
        return Result(PageConfigurationKnowledge.Unsupported, PageConfigurationApplicability.Applicable,
            "ConfigurationKnowledgeAndValueInconsistent");
    }

    private static PageBaseTypeConfigurationEvidence Unsupported(string reason) =>
        new("Unsupported", null, null, null, reason);

    private static bool ValidScope(PageBaseTypeFileScope scope, out Guid site, out Guid web, out Guid file)
    {
        site = web = file = Guid.Empty;
        return scope != null && Guid.TryParse(scope.SiteCollectionId, out site) && site != Guid.Empty &&
            Guid.TryParse(scope.WebId, out web) && web != Guid.Empty &&
            Guid.TryParse(scope.FileUniqueId, out file) && file != Guid.Empty &&
            LiteralFilePath(scope.ServerRelativePath);
    }

    private static bool LiteralFilePath(string path) => path != null && path.StartsWith('/') && !path.StartsWith("//", StringComparison.Ordinal) &&
        path.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase) && path == path.Trim() &&
        path.IndexOfAny(new[] { '*', '?', '#', '\\' }) < 0 && !path.Split('/').Any(part => part is "." or "..");

    private static bool ServerRelativePath(string url, out string path)
    {
        path = url;
        if (url != null && !url.StartsWith('/') && Uri.TryCreate(url, UriKind.Absolute, out var absolute) &&
            absolute.Scheme is "http" or "https" && absolute.Query.Length == 0 && absolute.Fragment.Length == 0)
            path = absolute.AbsolutePath;
        return LiteralFilePath(path);
    }
}
