using PnP.Scanning.Core.Scanners.WebPartMapping;
using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.Scanners
{
    /// <summary>
    /// Computes a classic page's "page transformation readiness" from its already-extracted web part
    /// inventory. For each <see cref="ClassicPageWebPart"/> it decides whether the part is mappable
    /// (has a modern equivalent) via the <see cref="WebPartMappingManager"/>, then rolls that up into
    /// the page's <see cref="ClassicPage.MappingPercentage"/>, the comma-separated
    /// <see cref="ClassicPage.UnmappedWebParts"/> short-type list, and the
    /// <see cref="ClassicPage.WebPartCount"/>.
    /// <para>
    /// Pure computation over the rows produced by <see cref="PageWebPartExtractor"/> — no CSOM — so it
    /// is fully unit-testable. Mirrors the legacy Modernization Scanner's per-page mapping logic
    /// (the MappingPercentage / UnmappedWebParts computation in <c>ModernizationScanJob</c>'s page CSV
    /// generation): the lookup keys on the assembly-qualified type, the unmapped list carries the
    /// de-duplicated short (class) names in page-layout order, and an empty page counts as 100%.
    /// </para>
    /// </summary>
    internal static class PageMappingCalculator
    {
        /// <summary>
        /// Applies the mapping verdict to every web part and rolls the result up onto the page. Mutates
        /// both <paramref name="page"/> (sets <see cref="ClassicPage.WebPartCount"/>,
        /// <see cref="ClassicPage.MappingPercentage"/>, <see cref="ClassicPage.UnmappedWebParts"/>) and
        /// each web part (sets <see cref="ClassicPageWebPart.IsMappable"/>).
        /// </summary>
        /// <param name="page">The classic page whose rollups are computed.</param>
        /// <param name="webParts">The page's extracted web part rows (may be null or empty).</param>
        /// <param name="mappingManager">The web part mapping lookup (embedded <c>webpartmapping.xml</c>).</param>
        internal static void ApplyMapping(ClassicPage page, IReadOnlyList<ClassicPageWebPart> webParts, WebPartMappingManager mappingManager)
        {
            ArgumentNullException.ThrowIfNull(page);
            ArgumentNullException.ThrowIfNull(mappingManager);

            int count = webParts?.Count ?? 0;
            page.WebPartCount = count;

            if (count == 0)
            {
                // Empty-page convention (parity with the legacy scanner): a page with no web parts has
                // nothing blocking its modernization, so it counts as fully mappable.
                page.MappingPercentage = 100;
                page.UnmappedWebParts = "";
                return;
            }

            int mapped = 0;
            var unmapped = new List<string>();

            // Process in page-layout order (row, then column, then order) so the unmapped list reads in
            // the same order as the legacy scanner emitted it.
            foreach (var webPart in webParts.OrderBy(w => w.Row).ThenBy(w => w.Column).ThenBy(w => w.Order))
            {
                bool isMappable = mappingManager.IsMappable(webPart.WebPartType);
                webPart.IsMappable = isMappable;

                if (isMappable)
                {
                    mapped++;
                }
                else if (!string.IsNullOrEmpty(webPart.WebPartTypeShort) && !unmapped.Contains(webPart.WebPartTypeShort))
                {
                    // The de-duplicated short (class) name, first occurrence winning.
                    unmapped.Add(webPart.WebPartTypeShort);
                }
            }

            // Rounded to a whole number to match the legacy per-page value (the old scanner formatted the
            // percentage with "{0:0}"); stored in a real column for downstream averaging/reporting.
            page.MappingPercentage = Math.Round((double)mapped / count * 100, MidpointRounding.AwayFromZero);
            page.UnmappedWebParts = string.Join(",", unmapped);
        }
    }
}
