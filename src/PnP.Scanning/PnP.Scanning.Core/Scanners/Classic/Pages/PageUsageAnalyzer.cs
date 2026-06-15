using Microsoft.SharePoint.Client;
using Microsoft.SharePoint.Client.Search.Query;
using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.Scanners
{
    /// <summary>
    /// Fills a classic page's usage statistics (recent / lifetime views and their unique-user counts)
    /// from SharePoint search, mirroring the legacy Modernization Scanner (its <c>ViewsRecent</c> /
    /// <c>ViewsLifeTime</c> managed-property merge in <c>PageAnalyzer</c>).
    /// <para>
    /// The legacy scanner ran a single bulk search per site collection and paged the results on
    /// <c>IndexDocId</c> — the source of a known paging failure on some tenants
    /// (see <c>modernization-scanner-indexdocid-search-paging</c>). That risk belonged to <b>discovery</b>,
    /// which the new engine performs over REST and did not port. Here usage is a <b>per-page lookup</b>
    /// (<see cref="QueryPageUsageAsync"/> with <c>RowLimit 1</c>), so there is no deep paging and the
    /// IndexDocId risk does not apply.
    /// </para>
    /// <para>
    /// Only the row parsing (<see cref="ParseSearchRow"/>), the skip-usage gate (<see cref="ApplyUsage"/>)
    /// and the search-path builder (<see cref="BuildPageSearchPath"/>) are pure and unit-tested; the live
    /// CSOM search query (<see cref="QueryPageUsageAsync"/>) is integration-only (→ T15).
    /// </para>
    /// </summary>
    internal static class PageUsageAnalyzer
    {
        // Local SharePoint Results result source (the well-known id the legacy scanner used) so the usage
        // query runs against the local search index.
        private static readonly Guid LocalSharePointResultsSourceId = new("8413cd39-2156-4e00-b54d-11efd9abdb89");

        // Managed properties carrying the page view statistics.
        internal const string OriginalPathProperty = "OriginalPath";
        internal const string ViewsRecentProperty = "ViewsRecent";
        internal const string ViewsRecentUniqueUsersProperty = "ViewsRecentUniqueUsers";
        internal const string ViewsLifeTimeProperty = "ViewsLifeTime";
        internal const string ViewsLifeTimeUniqueUsersProperty = "ViewsLifeTimeUniqueUsers";

        // The managed properties requested from search for a page.
        internal static readonly IReadOnlyList<string> PropertiesToRetrieve = new[]
        {
            OriginalPathProperty,
            ViewsRecentProperty,
            ViewsRecentUniqueUsersProperty,
            ViewsLifeTimeProperty,
            ViewsLifeTimeUniqueUsersProperty,
        };

        /// <summary>The four view counts parsed from a single search result row.</summary>
        internal readonly record struct PageUsageStatistics(
            int ViewsRecent, int ViewsRecentUniqueUsers, int ViewsLifeTime, int ViewsLifeTimeUniqueUsers);

        /// <summary>
        /// Pure: maps a search result row (managed property name → string value) onto the four view counts.
        /// A missing, empty or non-numeric value yields 0 (parity with the legacy <c>ToInt32()</c> merge).
        /// </summary>
        internal static PageUsageStatistics ParseSearchRow(IReadOnlyDictionary<string, string> searchRow)
        {
            if (searchRow == null)
            {
                return default;
            }

            return new PageUsageStatistics(
                ParseViewCount(searchRow, ViewsRecentProperty),
                ParseViewCount(searchRow, ViewsRecentUniqueUsersProperty),
                ParseViewCount(searchRow, ViewsLifeTimeProperty),
                ParseViewCount(searchRow, ViewsLifeTimeUniqueUsersProperty));
        }

        /// <summary>
        /// Pure: applies the parsed usage statistics to <paramref name="page"/>, unless usage information
        /// is skipped or no search row was found — in either case the view columns are left at their
        /// default 0.
        /// </summary>
        internal static void ApplyUsage(ClassicPage page, IReadOnlyDictionary<string, string> searchRow, bool skipUsageInformation)
        {
            ArgumentNullException.ThrowIfNull(page);

            if (skipUsageInformation || searchRow == null)
            {
                return;
            }

            var usage = ParseSearchRow(searchRow);
            page.ViewsRecent = usage.ViewsRecent;
            page.ViewsRecentUniqueUsers = usage.ViewsRecentUniqueUsers;
            page.ViewsLifeTime = usage.ViewsLifeTime;
            page.ViewsLifeTimeUniqueUsers = usage.ViewsLifeTimeUniqueUsers;
        }

        /// <summary>
        /// Pure: builds the absolute page URL used to look a page up in search. The home page is indexed
        /// under the web URL itself (not its <c>SitePages/...aspx</c> path), matching the legacy scanner;
        /// any other page's server-relative <paramref name="pageUrl"/> is prefixed with the site authority.
        /// </summary>
        internal static string BuildPageSearchPath(string siteUrl, string webUrl, string pageUrl, bool isHomePage)
        {
            if (isHomePage)
            {
                return $"{siteUrl}{webUrl}";
            }

            // pageUrl is server-relative (e.g. /sites/foo/SitePages/Page.aspx); prefix the site authority.
            string authority = new Uri(siteUrl).GetLeftPart(UriPartial.Authority);
            return $"{authority}{pageUrl}";
        }

        private static int ParseViewCount(IReadOnlyDictionary<string, string> row, string property)
        {
            if (row.TryGetValue(property, out var value) && int.TryParse(value, out var parsed))
            {
                return parsed;
            }

            return 0;
        }

        /// <summary>
        /// Integration-only (exercised by the T15 live test): runs a per-page search lookup and returns the
        /// requested managed property values as a row dictionary, or <c>null</c> when the page is not in the
        /// search index. A per-page lookup (<c>RowLimit 1</c>) deliberately avoids the legacy bulk-search
        /// IndexDocId paging risk.
        /// </summary>
        internal static async Task<IReadOnlyDictionary<string, string>> QueryPageUsageAsync(ClientContext csomContext, string searchPath)
        {
            var keywordQuery = new KeywordQuery(csomContext)
            {
                QueryText = $"path={searchPath} AND fileextension=aspx",
                RowLimit = 1,
                TrimDuplicates = false,
                SourceId = LocalSharePointResultsSourceId,
            };

            foreach (var property in PropertiesToRetrieve)
            {
                keywordQuery.SelectProperties.Add(property);
            }

            var searchExecutor = new SearchExecutor(csomContext);
            var results = searchExecutor.ExecuteQuery(keywordQuery);
            await csomContext.ExecuteQueryAsync().ConfigureAwait(false);

            var table = results.Value?.FirstOrDefault();
            if (table == null || table.RowCount == 0)
            {
                return null;
            }

            var firstRow = table.ResultRows.First();
            var row = new Dictionary<string, string>();
            foreach (var property in PropertiesToRetrieve)
            {
                row[property] = firstRow.TryGetValue(property, out var value) && value != null ? value.ToString() : "";
            }

            return row;
        }
    }
}
