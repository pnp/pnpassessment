using ClassicPageDiscovery = PnP.Scanning.Core.Pipeline.Contracts.ClassicPageDiscoveryRow;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Pipeline.Contracts;

using System.Text.Json;
namespace PnP.Scanning.Core.Pipeline.Analysis;

internal static class ClassicPageCoverage
{
    internal static ClassicPageDiscovery Create(Guid scanId, IReadOnlyList<ClassicPageDiscovery> allRows, DateTime observedAtUtc)
    {
            var coverageRows = allRows.Where(row => row.RowType is "Scope" or "Gap").ToArray();
            var scopes = coverageRows.Where(row => row.RowType == "Scope").ToArray();
            var selection = scopes.FirstOrDefault(row => row.RecordKey == "assessment:site-selection");
            var pageSelection = scopes.FirstOrDefault(row => row.RecordKey == "assessment:page-selection");
            var statuses = coverageRows.Select(row => row.DiscoveryStatus)
                .Concat(allRows.Where(row => row.RowType == "Reference" ||
                    (row.RowType == "Pagination" && row.DiscoveryStatus is ("Denied" or "Failed" or "Unknown")))
                    .Select(row => row.DiscoveryStatus)).ToArray();
            var gapCodes = allRows.SelectMany(row => (row.ErrorCodes ?? string.Empty)
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Select(code => code.Contains(':') ? code[(code.LastIndexOf(':') + 1)..] : code)
                .ToArray();

            DiscoveryVerdict verdict;
            if (scopes.Length == 0 || selection?.ScopeType is not ("Tenant" or "SiteSelection") ||
                statuses.Any(status => status is "Pending" or "Unknown") ||
                gapCodes.Any(DiscoveryGapCodes.ForcesUnknown))
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

            var pageCount = allRows.Count(row => row.RowType == "Page");
            var gapCount = coverageRows.Count(row => row.DiscoveryStatus is not ("Complete" or "Empty" or "PolicyExcluded"));
            return new ClassicPageDiscovery
            {
                ScanId = scanId,
                RecordKey = "summary:coverage",
                RowType = "Summary",
                ScopeType = "Assessment",
                Url = selection?.Url,
                ObservationMethod = pageSelection?.ObservationMethod ?? selection?.ObservationMethod,
                DiscoveryStatus = verdict.ToString(),
                ExpectedChildCount = selection?.ExpectedChildCount,
                ObservedChildCount = pageCount,
                ErrorCodes = string.Join(';', gapCodes.Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)),
                EvidenceJson = JsonSerializer.Serialize(new
                {
                    verdict = verdict.ToString(),
                    declaredScope = selection?.ScopeType == "SiteSelection" || pageSelection != null,
                    pageScope = pageSelection?.ObservationMethod ?? AspxDiscoveryIntent.FullInventory.ToString(),
                    siteCount = selection?.ObservedChildCount,
                    pageCount,
                    scopeCount = scopes.Length,
                    incompleteScopeCount = gapCount,
                }),
                ObservedAtUtc = observedAtUtc,
            };
    }
}
