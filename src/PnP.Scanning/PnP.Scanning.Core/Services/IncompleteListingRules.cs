#nullable enable
using System.Text.Json;

namespace PnP.Scanning.Core.Services;

/// <summary>
/// Decides which stored coverage rows of an assessment are locations whose files may be missing from the report.
/// </summary>
internal static class IncompleteListingRules
{
    /// <summary>
    /// Names the second, reduced listing of the files of a folder, which runs when the first listing fails.
    /// </summary>
    internal const string SecondFileListing = "PhysicalFolderFilesAuthorityFallback";

    /// <summary>Names the scope type of a row that describes one request to SharePoint.</summary>
    private const string Surface = "Surface";

    /// <summary>Names the first listings of the files of a folder, which the second listing repeats.</summary>
    private static readonly HashSet<string> FirstFileListings = new(StringComparer.Ordinal)
    {
        "DocumentLibraryFilesAuthority", "ListRootFilesAuthority", "PhysicalFormsTreeAuthority",
    };

    /// <summary>
    /// Holds the columns of a coverage row whose status is other than Complete, Empty or PolicyExcluded.
    /// </summary>
    internal sealed record OpenRow(
        string SiteUrl,
        string WebUrl,
        string RecordKey,
        string? ParentScopeKey,
        string? ScopeType,
        string? ObservationMethod,
        string? EvidenceJson);

    /// <summary>
    /// Counts the rows of <paramref name="open"/> that stand for files the inventory may lack. Two kinds of rows
    /// hold no missing file and stay out of the count: a first file listing that failed while the second listing of
    /// the same folder, named in <paramref name="secondListingCompleted"/> by site, web and parent scope, finished;
    /// and a request that a reviewed rule expects SharePoint to refuse, together with the scope that failed only
    /// because of such requests.
    /// </summary>
    internal static int CountLocations(
        IReadOnlyCollection<OpenRow> open,
        IReadOnlySet<(string SiteUrl, string WebUrl, string ParentScopeKey)> secondListingCompleted)
    {
        OpenRow[] rows = open.ToArray();
        bool[] expected = new bool[rows.Length];
        Dictionary<(string, string, string), (int Open, int Expected)> requests = new();
        for (int index = 0; index < rows.Length; index++)
        {
            OpenRow row = rows[index];
            if (row.ScopeType != Surface || row.ParentScopeKey == null) continue;
            expected[index] = RefusalExpected(row);
            (string, string, string) parent = (row.SiteUrl, row.WebUrl, row.ParentScopeKey);
            (int Open, int Expected) seen = requests.GetValueOrDefault(parent);
            requests[parent] = (seen.Open + 1, seen.Expected + (expected[index] ? 1 : 0));
        }

        int locations = 0;
        for (int index = 0; index < rows.Length; index++)
        {
            OpenRow row = rows[index];
            if (expected[index]) continue;
            if (row.ScopeType == Surface)
            {
                if (row.ParentScopeKey != null && row.ObservationMethod != null &&
                    FirstFileListings.Contains(row.ObservationMethod) &&
                    secondListingCompleted.Contains((row.SiteUrl, row.WebUrl, row.ParentScopeKey)))
                    continue;
            }
            else if (requests.TryGetValue((row.SiteUrl, row.WebUrl, row.RecordKey), out var own) &&
                     own.Open == own.Expected)
            {
                continue;
            }

            locations++;
        }

        return locations;
    }

    /// <summary>
    /// Checks whether the evidence of a request names the reviewed rule that expected SharePoint to refuse it.
    /// Evidence that cannot be read names no rule.
    /// </summary>
    internal static bool RefusalExpected(OpenRow row)
    {
        if (string.IsNullOrWhiteSpace(row.EvidenceJson)) return false;
        try
        {
            using JsonDocument evidence = JsonDocument.Parse(row.EvidenceJson);
            return evidence.RootElement.ValueKind == JsonValueKind.Object &&
                   evidence.RootElement.TryGetProperty("applicabilityRuleId", out JsonElement rule) &&
                   rule.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(rule.GetString());
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
