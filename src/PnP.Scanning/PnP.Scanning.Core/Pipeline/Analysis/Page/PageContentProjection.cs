#nullable enable
using System.Text.Json;
using PnP.Scanning.Core.Pipeline.Analysis.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Page;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Pipeline.Contracts.Web;

namespace PnP.Scanning.Core.Pipeline.Analysis.Page;

/// <summary>
/// Unifies file-backed and independent-item candidates by discriminated native identity, never URL, renderer, or hash.
/// </summary>
internal static class PageContentProjection
{
    /// <summary>
    /// Keeps one Page per native object and retains inventory-only files even when item enrichment or metadata was unavailable.
    /// </summary>
    internal static ClassicPageRow[] Build(
        Guid assessmentId,
        ClassicContentIndex index,
        IEnumerable<ClassicPageRow> candidates)
    {
        Dictionary<PageIdentity, ClassicPageRow> pages = [];
        foreach (ClassicPageRow candidate in candidates)
        {
            PageIdentity identity = candidate.Identity?.Validated() ??
                                    throw new SnapshotIntegrityException(
                                        "A projected Page has no observed native identity.");
            if (candidate.ScanId != assessmentId || candidate.SiteCollectionId != identity.SiteCollectionId)
                throw new SnapshotIntegrityException("A projected Page has foreign assessment or site ownership.");
            if (identity.ObjectKind == PageObjectKind.File && candidate.FileUniqueId != identity.FileUniqueId ||
                identity.ObjectKind == PageObjectKind.ListItem && (candidate.FileUniqueId != null ||
                                                                   candidate.ListId != identity.ListId ||
                                                                   candidate.ListItemId != identity.ListItemId))
                throw new SnapshotIntegrityException("A Page's native identity differs from its object metadata.");
            if (pages.TryGetValue(identity, out ClassicPageRow? previous))
            {
                if (VersionedJson.From(previous).Json != VersionedJson.From(candidate).Json)
                    throw new SnapshotIntegrityException(
                        $"Conflicting Page projections for native content {identity.Key}.");
                continue;
            }

            pages.Add(identity, candidate);
        }

        foreach (PhysicalFileFacts file in index.Files.Values)
        {
            WebFacts web = index.Webs[CollectionJson.WebKey(file.SiteUrl!, file.WebUrl!)];
            bool? homePage = SnapshotHomePage.ResolveHomePageState(
                file.ServerRelativeUrl!,
                web.WelcomePage,
                web.WelcomePageState.Succeeded,
                null);
            if (index.Scope.Options.HomePageOnly && homePage != true) continue;
            bool aspx = file.FileName?.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase) == true ||
                        file.ServerRelativeUrl?.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase) == true;
            bool pointPublishing = file.FileName?.EndsWith(".pointpub", StringComparison.OrdinalIgnoreCase) == true ||
                                   file.ServerRelativeUrl?.EndsWith(".pointpub", StringComparison.OrdinalIgnoreCase) ==
                                   true || string.Equals(file.FileType, "pointpub", StringComparison.OrdinalIgnoreCase);
            if (!aspx && !pointPublishing) continue;
            PageIdentity identity = PageIdentity.File(file.SiteCollectionId, file.FileUniqueId);
            if (pages.ContainsKey(identity)) continue;
            PhysicalFileAnalysis classification = PhysicalFileAnalysisRules.Analyze(file);
            pages.Add(
                identity,
                new ClassicPageRow
                {
                    Identity = identity,
                    ScanId = assessmentId,
                    SiteCollectionId = file.SiteCollectionId,
                    FileUniqueId = file.FileUniqueId,
                    WebId = file.WebId,
                    SiteUrl = file.SiteUrl,
                    WebUrl = file.WebUrl,
                    ListId = file.ListId,
                    ListItemId = file.ListItemId,
                    PageUrl = file.ServerRelativeUrl!,
                    PageName = file.FileName,
                    PageType =
                        pointPublishing
                            ? ClassicPageRules.DelveBlogPage
                            : classification.Classification?.PageType ?? "Unknown",
                    SourceKind = "File",
                    ContentReadState = ReadContentState(file),
                    DiscoveryStatus = file.CoverageState ?? "Observed",
                    AssessmentStatus = "Unknown",
                    ProductFamily = classification.Classification?.ProductFamily ?? "Unknown",
                    Experience = classification.Classification?.Experience ?? "Unknown",
                    ImpactStatus = classification.Classification?.ImpactStatus ?? "Unknown",
                    ImpactedBy = classification.Classification?.ImpactedBy,
                    ClassificationReason =
                        classification.Classification?.Reason ?? "PhysicalFileOnly;ItemEnrichmentUnavailable",
                    CustomizedPageStatus = file.CustomizedPageStatus,
                    HomePage = homePage,
                });
        }

        foreach (ClassicPageRow page in pages.Values.Where(page => page.Identity!.ObjectKind == PageObjectKind.File))
        {
            PhysicalFileFacts file =
                index.Files.GetValueOrDefault((page.Identity!.SiteCollectionId, page.Identity.FileUniqueId!.Value)) ??
                throw new SnapshotIntegrityException("A file-backed Page has no acquired physical facts.");
            if (page.ListId != null && file.ListId != null && page.ListId != file.ListId || page.ListItemId != null &&
                file.ListItemId != null && page.ListItemId != file.ListItemId)
                throw new SnapshotIntegrityException(
                    "A Page and physical file have conflicting backing-item metadata.");
            page.ListId = file.ListId;
            page.ListItemId = file.ListItemId;
            page.ContentReadState ??= ReadContentState(file);
            if (file.AspxEvidenceJson != null)
            {
                PhysicalFileAnalysis classification = PhysicalFileAnalysisRules.Analyze(file);
                page.ProductFamily = classification.Classification?.ProductFamily ?? "Unknown";
                page.Experience = classification.Classification?.Experience ?? "Unknown";
                page.ImpactStatus = classification.Classification?.ImpactStatus ?? "Unknown";
                page.ImpactedBy = classification.Classification?.ImpactedBy;
                page.ClassificationReason = classification.Classification?.Reason;
            }
        }

        return pages.Values.OrderBy(page => page.Identity!.Key, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Reports original-byte acquisition independently of item metadata or a native file association.
    /// </summary>
    private static string ReadContentState(PhysicalFileFacts file) =>
        file.AspxEvidenceJson == null
            ? "NotAttempted"
            : JsonSerializer.Deserialize<AspxPageEvidence>(file.AspxEvidenceJson)?.SourceReadState ?? "Unknown";
}