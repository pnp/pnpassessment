using System.Text.Json;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.ClassicPages.Contracts.Page;

namespace PnP.Scanning.Core.ClassicPages.Analysis.Page;

/// <summary>Reconciles native discovery evidence and computes deterministic coverage independently of persistence.</summary>
internal static class DiscoveryEvidenceRules
{
    /// <summary>
    /// Builds a deterministic coverage verdict from scope, pagination, reference, and gap evidence.
    /// </summary>
    internal static DiscoveryEvidence BuildCoverage(
        Guid scanId,
        IReadOnlyList<DiscoveryEvidence> allRows,
        DateTime observedAtUtc)
    {
        DiscoveryEvidence[] coverageRows =
            allRows.Where(row => row.RowType is DiscoveryRowType.Scope or DiscoveryRowType.Gap).ToArray();
        DiscoveryEvidence[] scopes = coverageRows.Where(row => row.RowType == DiscoveryRowType.Scope).ToArray();
        DiscoveryEvidence selection = scopes.FirstOrDefault(row => row.RecordKey == "assessment:site-selection");
        DiscoveryEvidence pageSelection = scopes.FirstOrDefault(row => row.RecordKey == "assessment:page-selection");
        string[] statuses = coverageRows.Select(row => row.DiscoveryStatus)
            .Concat(
                allRows.Where(row =>
                        row.RowType == DiscoveryRowType.Reference || (row.RowType == DiscoveryRowType.Pagination &&
                                                                      row.DiscoveryStatus is ("Denied" or "Failed"
                                                                          or "Unknown")))
                    .Select(row => row.DiscoveryStatus))
            .ToArray();
        string[] gapCodes = allRows.Where(row => row.RowType != DiscoveryRowType.Assessment)
            .SelectMany(row => (row.ErrorCodes ?? string.Empty).Split(
                ';',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(code => code.Contains(':') ? code[(code.LastIndexOf(':') + 1)..] : code)
            .ToArray();

        DiscoveryVerdict verdict;
        if (scopes.Length == 0 || selection?.ScopeType is not ("Tenant" or "SiteSelection") ||
            statuses.Any(status => status is "Pending" or "Unknown") || gapCodes.Any(DiscoveryGapCodes.ForcesUnknown))
        {
            verdict = DiscoveryVerdict.Unknown;
        }
        else if (statuses.Any(status => status is "Denied" or "Failed" or "Partial" or "Cancelled") ||
                 gapCodes.Contains(DiscoveryGapCodes.ExpectedChildMissing, StringComparer.Ordinal))
        {
            verdict = DiscoveryVerdict.Incomplete;
        }
        else if (pageSelection?.ObservationMethod == "HomePageOnly" || selection?.ScopeType == "SiteSelection")
        {
            verdict = DiscoveryVerdict.CompleteDeclaredSubset;
        }
        else
        {
            verdict = DiscoveryVerdict.CompleteTenantVerified;
        }

        int pageCount = allRows.Count(row => row.RowType == DiscoveryRowType.Page);
        int gapCount =
            coverageRows.Count(row => row.DiscoveryStatus is not ("Complete" or "Empty" or "PolicyExcluded"));
        DiscoveryEvidence current = new DiscoveryEvidence
        {
            ScanId = scanId,
            RecordKey = "summary:coverage",
            RowType = DiscoveryRowType.Summary,
            ScopeType = "Assessment",
            Url = selection?.Url,
            ObservationMethod = pageSelection?.ObservationMethod ?? selection?.ObservationMethod,
            DiscoveryStatus = verdict.ToString(),
            ExpectedChildCount = selection?.ExpectedChildCount,
            ObservedChildCount = pageCount,
            ErrorCodes =
                string.Join(
                    ';',
                    gapCodes.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal)),
            EvidenceJson = JsonSerializer.Serialize(
                new
                {
                    verdict = verdict.ToString(),
                    declaredScope = selection?.ScopeType == "SiteSelection" || pageSelection != null,
                    pageScope =
                        pageSelection?.ObservationMethod ?? AspxDiscoveryIntent.FullInventory.ToString(),
                    siteCount = selection?.ObservedChildCount,
                    pageCount,
                    scopeCount = scopes.Length,
                    incompleteScopeCount = gapCount,
                }),
            ObservedAtUtc = observedAtUtc,
        };
        return current;
    }

    /// <summary>
    /// Preserves richer observed file metadata and reports identity conflicts when discovery surfaces return the same record.
    /// </summary>
    internal static void Reconcile(DiscoveryEvidence previous, DiscoveryEvidence row)
    {
        // Observing the same file through Forms/Views and raw files must not erase
        // richer metadata previously read through its list-item surface.
        if (row.RowType == DiscoveryRowType.Page)
        {
            List<string> changed = new List<string>();
            if (!string.Equals(previous.Url, row.Url, StringComparison.OrdinalIgnoreCase)) changed.Add("Url");
            if (previous.ListId.HasValue && row.ListId.HasValue && previous.ListId != row.ListId) changed.Add("ListId");
            if (previous.ListItemId.HasValue && row.ListItemId.HasValue && previous.ListItemId != row.ListItemId)
                changed.Add("ListItemId");
            if (previous.HomePage.HasValue && row.HomePage.HasValue && previous.HomePage != row.HomePage)
                changed.Add("HomePage");
            if (previous.ContentTypeId != null && row.ContentTypeId != null &&
                previous.ContentTypeId != row.ContentTypeId)
                changed.Add("ContentTypeId");
            if (previous.CustomizedPageStatus != null && row.CustomizedPageStatus != null &&
                previous.CustomizedPageStatus != row.CustomizedPageStatus)
                changed.Add("CustomizedPageStatus");
            if (changed.Count != 0)
                AddError(
                    row,
                    "DiscoveryMetadata",
                    DiscoveryGapCodes.ChangedDuringScan,
                    "Repeated file identity changed: " + string.Join(", ", changed));
            row.ListId ??= previous.ListId;
            row.FolderUniqueId ??= previous.FolderUniqueId;
            row.ListItemId ??= previous.ListItemId;
            row.ContentTypeId ??= previous.ContentTypeId;
            row.PageType ??= previous.PageType;
            row.SetupPath ??= previous.SetupPath;
            row.HomePage ??= previous.HomePage;
            row.LibraryHidden ??= previous.LibraryHidden;
            row.CustomizedPageStatus ??= previous.CustomizedPageStatus;
            row.AssessmentStatus ??= previous.AssessmentStatus;
        }
        else if (row.RowType == DiscoveryRowType.Reference)
        {
            if (previous.FileUniqueId.HasValue && row.FileUniqueId.HasValue &&
                previous.FileUniqueId != row.FileUniqueId)
            {
                row.DiscoveryStatus = "Unknown";
                AddError(
                    row,
                    "ReferenceReconciliation",
                    DiscoveryGapCodes.MetadataConflict,
                    "Repeated reference identity resolved to a different physical file.");
                row.FileUniqueId = previous.FileUniqueId;
            }

            row.FileUniqueId ??= previous.FileUniqueId;
        }
        else if (row.RowType == DiscoveryRowType.Pagination && previous.EvidenceJson != null &&
                 row.EvidenceJson != null && previous.EvidenceJson != row.EvidenceJson)
        {
            row.DiscoveryStatus = "Unknown";
            AddError(
                row,
                "PaginationReconciliation",
                DiscoveryGapCodes.ChangedDuringScan,
                "Repeated pagination evidence changed for the same request identity.");
        }
        else if (row.RowType == DiscoveryRowType.Scope && row.ScopeType == "Surface" &&
                 !TerminalSuccess(previous.DiscoveryStatus) && TerminalSuccess(row.DiscoveryStatus))
        {
            // A later successful visit cannot erase a retained denied/failed/unknown surface.
            row.DiscoveryStatus = previous.DiscoveryStatus;
            row.EvidenceJson = previous.EvidenceJson;
        }

        row.ErrorStage = Join(previous.ErrorStage, row.ErrorStage);
        row.ErrorCodes = Join(previous.ErrorCodes, row.ErrorCodes);
        row.ErrorDetail = Join(previous.ErrorDetail, row.ErrorDetail, "\n");
        row.EvidenceJson ??= previous.EvidenceJson;
    }

    /// <summary>Unions diagnostic blocks without splitting multiline messages and orders distinct delimited codes.</summary>
    internal static string Join(string left, string right, string separator = ";")
    {
        IEnumerable<string> values = new[] { left, right }.Where(value => !string.IsNullOrWhiteSpace(value));
        // Preserve complete human-readable messages, including embedded newlines and commas.
        if (separator == "\n") return string.Join(separator, values.Distinct(StringComparer.Ordinal));
        return string.Join(
            separator,
            values.SelectMany(value => value.Split(separator, StringSplitOptions.RemoveEmptyEntries))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal));
    }

    /// <summary>Recognizes successful or explicitly excluded discovery dispositions.</summary>
    private static bool TerminalSuccess(string status) =>
        status is "Complete" or "Empty" or "PolicyExcluded" or "Discovered";

    /// <summary>Retains distinct stages, codes and complete error messages on native discovery evidence.</summary>
    internal static void AddError(DiscoveryEvidence row, string stage, string code, string detail)
    {
        if (string.IsNullOrWhiteSpace(code) && string.IsNullOrWhiteSpace(detail)) return;
        row.ErrorStage = Join(row.ErrorStage, stage);
        row.ErrorCodes = Join(row.ErrorCodes, code);
        row.ErrorDetail = Join(row.ErrorDetail, detail, "\n");
    }
}