using PnP.Scanning.Core.Storage;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PnP.Scanning.Core.Discovery;

/// <summary>Source-declared evidence for one family, never an observed runtime handler.</summary>
internal static class PublishingLayoutTypeEvidence
{
    internal sealed record Observation(string Declaration, string ResolvedIdentity, string SourceHash,
        string SourceStatus, string ResolutionStatus, string Decision, string Reason,
        PublishingLayoutTypeCatalog.TypeEdge[] Ancestry, string[] CatalogErrors, string DirectiveEvidence = null,
        string SourceHashKind = AspxSourceReadResult.LegacySourceHashKind, string ParseStatus = null, string ParseReason = null);

    internal static bool IsConfirmedMember(ClassicPageDiscovery row) => row.RowType == "Page" &&
        row.PageTypeEvidenceOrigin == "DeclaredSource" && row.PageTypeSourceStatus == "Available" &&
        row.PageTypeResolutionStatus == "Resolved" && row.PublishingLayoutFamily == "Member";

    internal static Func<ClassicPageDiscovery, CancellationToken, Task> ForScan(Scan scan,
        Func<ClassicPageDiscovery, CancellationToken, Task<string>> readSource)
    {
        if (scan.PublishingLayoutRuleVersion != PublishingLayoutTypeCatalog.CurrentRuleVersion) return null;
        var catalog = PublishingLayoutTypeCatalog.FromJson(scan.PublishingLayoutTypeCatalogJson);
        return (row, token) => AcquireAsync(row, readSource, catalog, token);
    }

    internal static async Task AcquireAsync(ClassicPageDiscovery row, Func<ClassicPageDiscovery, CancellationToken, Task<string>> readSource,
        PublishingLayoutTypeCatalog catalog, CancellationToken token)
    {
        Observation observation;
        try
        {
            token.ThrowIfCancellationRequested();
            var source = await readSource(row, token).ConfigureAwait(false);
            observation = Inspect(source, catalog);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var status = AssessmentWebDiscovery.Status(AssessmentWebDiscovery.Classify(ex));
            observation = new(null, null, null, status, "Unknown", "Unknown",
                "Source" + status + ":" + AssessmentWebDiscovery.ErrorCode(ex), Array.Empty<PublishingLayoutTypeCatalog.TypeEdge>(), catalog.Errors.ToArray());
        }
        var observations = Read(row).Append(observation);
        Apply(row, observations);
    }

    internal static Observation Inspect(string source, PublishingLayoutTypeCatalog catalog)
    {
        var hash = source == null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
        return InspectParsed(source, hash, PageDirectiveParser.Parse(source), catalog);
    }

    private static Observation InspectParsed(string source, string hash, PageDirectiveParseResult parsed, PublishingLayoutTypeCatalog catalog)
    {
        var directiveEvidence = source == null ? null : parsed.RawPageDirectiveEvidence;
        Observation Unknown(string reason, string declaration = null) => new(declaration, null, hash,
            source == null ? "Unknown" : "Available", "Unknown", "Unknown", reason,
            Array.Empty<PublishingLayoutTypeCatalog.TypeEdge>(), catalog.Errors.ToArray(), directiveEvidence,
            ParseStatus: parsed.Status.ToString(), ParseReason: parsed.Reason);
        if (!parsed.IsReliableDeclaration)
        {
            // Preserve the inherited family reasons while exposing the independent parser states.
            return Unknown(parsed.Status switch
            {
                PageDirectiveParseStatus.SourceUnavailable or PageDirectiveParseStatus.EmptySource => "SourceUnavailable",
                PageDirectiveParseStatus.VerifiedAbsent or PageDirectiveParseStatus.EmptyInherits => "InheritsMissing",
                PageDirectiveParseStatus.DuplicateAttribute => "AmbiguousPageAttribute",
                PageDirectiveParseStatus.DuplicatePageDirective => "AmbiguousPageDirective",
                PageDirectiveParseStatus.MalformedDirective => "MalformedPageDirective",
                PageDirectiveParseStatus.InspectionLimitExceeded => "DirectiveInspectionLimitExceeded",
                PageDirectiveParseStatus.LexicallyUncertain => "DirectiveInspectionUncertain:" + parsed.Reason,
                _ => "PageDirectiveMissing",
            });
        }
        var declaration = parsed.DeclaredInherits;
        if (parsed.HasDynamicCompilation) return Unknown("DynamicCompilationUnsupported", declaration);
        var bound = PublishingLayoutTypeCatalog.Bind(declaration);
        var result = catalog.Resolve(bound);
        return new(declaration, result.Chain.Length == 0 ? null : bound, hash, "Available", result.Status,
            result.Decision, result.Reason, result.Chain, catalog.Errors.ToArray(), directiveEvidence,
            ParseStatus: parsed.Status.ToString(), ParseReason: parsed.Reason);
    }

    internal static void ApplySourceRead(ClassicPageDiscovery row, AspxSourceReadResult result,
        PublishingLayoutTypeCatalog catalog) =>
        ApplySourceRead(row, result, catalog, PageDirectiveParser.Parse(result.DecodedText));

    internal static void ApplySourceRead(ClassicPageDiscovery row, AspxSourceReadResult result,
        PublishingLayoutTypeCatalog catalog, PageDirectiveParseResult parsed)
    {
        Observation observation;
        if (result.IsReliableSource)
        {
            observation = InspectParsed(result.DecodedText, result.LegacySourceHash, parsed, catalog);
        }
        else
        {
            var denied = result.TransportState == AspxSourceTransportState.Denied ||
                result.ContentState is AspxSourceContentState.LoginShell or AspxSourceContentState.SemanticDenied;
            var status = denied ? "Denied" : result.TransportState == AspxSourceTransportState.Failed ? "Failed" : "Unknown";
            observation = new(null, null, result.LegacySourceHash, status, "Unknown", "Unknown",
                "Source" + status + ":" + string.Join(';', result.TransportReason, result.ContentReason,
                    result.Decoding.Reason, result.PhysicalIdentity.Reason, result.IdentityComparisonReason),
                Array.Empty<PublishingLayoutTypeCatalog.TypeEdge>(), catalog.Errors.ToArray(),
                parsed.RawPageDirectiveEvidence, ParseStatus: parsed.Status.ToString(), ParseReason: parsed.Reason);
        }
        // Use the same conservative family merge as the legacy decoded-text API.
        Apply(row, Read(row).Append(observation));
    }

    internal static void Merge(ClassicPageDiscovery previous, ClassicPageDiscovery current) =>
        Apply(current, Read(previous).Concat(Read(current)));

    private static IEnumerable<Observation> Read(ClassicPageDiscovery row) => string.IsNullOrEmpty(row.PageTypeEvidenceJson)
        ? Array.Empty<Observation>() : JsonSerializer.Deserialize<Observation[]>(row.PageTypeEvidenceJson);

    private static void Apply(ClassicPageDiscovery row, IEnumerable<Observation> observations)
    {
        var retained = observations.GroupBy(value => JsonSerializer.Serialize(value), StringComparer.Ordinal)
            .Select(group => group.First()).ToArray();
        if (retained.Length == 0) return;
        row.PageTypeEvidenceJson = JsonSerializer.Serialize(retained);
        row.PageTypeEvidenceOrigin = "DeclaredSource";
        row.DeclaredPageType = string.Join("\n", retained.Select(value => value.Declaration)
            .Where(value => value != null).Distinct(StringComparer.Ordinal));
        if (row.DeclaredPageType.Length == 0) row.DeclaredPageType = null;
        var identities = retained.Select(value => value.ResolvedIdentity).Where(value => value != null).Distinct().ToArray();
        row.ResolvedPageType = identities.Length == 1 ? identities[0] : null;
        row.PageTypeSourceStatus = retained.Any(value => value.SourceStatus == "Denied") ? "Denied" :
            retained.Any(value => value.SourceStatus == "Failed") ? "Failed" :
            retained.Any(value => value.SourceStatus == "Unknown") ? "Unknown" : "Available";
        var decisions = retained.Select(value => value.Decision).Distinct().ToArray();
        var conflict = identities.Length > 1 || decisions.Contains("Member") && decisions.Contains("NonMember");
        row.PublishingLayoutFamily = conflict || decisions.Length != 1 ? "Unknown" : decisions[0];
        row.PageTypeResolutionStatus = row.PublishingLayoutFamily == "Unknown" ? "Unknown" : "Resolved";
        row.PageTypeReason = string.Join(';', retained.Select(value => value.Reason)
            .Concat(conflict ? new[] { "ConflictingTypeObservations" } : Array.Empty<string>()).Distinct().OrderBy(value => value, StringComparer.Ordinal));
    }
}
