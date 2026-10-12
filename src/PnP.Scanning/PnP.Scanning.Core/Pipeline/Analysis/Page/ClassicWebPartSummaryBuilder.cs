using ClassicPageWebPart = PnP.Scanning.Core.Pipeline.Contracts.Page.ClassicPageWebPartRow;
using ClassicWebPartUnique = PnP.Scanning.Core.Pipeline.Contracts.Page.ClassicWebPartUniqueRow;
using PnP.Scanning.Core.Pipeline.Analysis.Page.WebPartMapping;

namespace PnP.Scanning.Core.Pipeline.Analysis.Page;

/// <summary>
/// Summarizes analyzed web part types, distinct pages using each type and recognition by the fixed mapping catalog.
/// <para>Evolved from <c>Pipeline/Analysis/Module/ClassicPageReportBuilder.cs</c> to keep web part inventory aggregation in the Page subject.</para>
/// </summary>
internal static class ClassicWebPartSummaryBuilder
{
    /// <summary>
    /// Groups inventory rows by type and counts each physical page once in the existing unique web part report shape.
    /// <para>Evolved from <c>Pipeline/Analysis/Module/ClassicPageReportBuilder.cs</c> to separate page inventory calculations from report publication.</para>
    /// </summary>
    internal static IEnumerable<ClassicWebPartUnique> Build(Guid assessmentId,
        IEnumerable<ClassicPageWebPart> parts, WebPartMappingManager mapping) =>
        parts.GroupBy(x => x.WebPartType).Select(group => new ClassicWebPartUnique
        {
            ScanId = assessmentId, WebPartType = group.Key, InMappingFile = mapping.InMappingFile(group.Key),
            PageCount = group.Select(x => (x.SiteUrl, x.WebUrl, x.PageUrl)).Distinct().Count(),
        });
}
