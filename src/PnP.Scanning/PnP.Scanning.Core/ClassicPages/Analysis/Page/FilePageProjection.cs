using PnP.Scanning.Core.ClassicPages.Contracts.Module;
using PnP.Scanning.Core.ClassicPages.Contracts.Shared;
#nullable enable
using System.Text.Json;
using System.Text.RegularExpressions;
using PnP.Scanning.Core.ClassicPages.Contracts.Page;
using PnP.Scanning.Core.ClassicPages.Contracts.Web;
using PnP.Scanning.Core.Scanners.WebPartMapping;


namespace PnP.Scanning.Core.ClassicPages.Analysis.Page;

/// <summary>
/// Carries an assessed page, web parts, discovery disposition, and explicit failures.
/// </summary>
internal sealed record FilePageAnalysis(
    ClassicPageRow? Page,
    ClassicPageWebPartRow[] Parts,
    DiscoveryEvidence? Discovery,
    bool Modern,
    string? Error = null);

/// <summary>
/// Applies page readiness rules to detached evidence without modifying collected source.
/// </summary>
internal static class FilePageProjection
{
    /// <summary>
    /// Assesses captured page fields and web parts while retaining nullable selection and failure provenance.
    /// </summary>
    internal static FilePageAnalysis Analyze(
        Guid assessmentId,
        WebFacts web,
        DiscoveryEvidence discovered,
        FilePageEvidence source,
        ClassicCollectionOptions options,
        WebPartMappingManager mapping)
    {
        if (discovered.SiteCollectionId is not Guid site || site == Guid.Empty ||
            discovered.FileUniqueId is not Guid file || file == Guid.Empty)
            return new(
                null,
                [],
                discovered,
                false,
                "Physical file identity was not acquired; no file Page can be invented.");
        FilePageAnalysis result = AnalyzeCapturedEvidence(assessmentId, web, discovered, source, options, mapping);
        if (result.Modern)
            result = result with
            {
                Page = new ClassicPageRow
                {
                    ScanId = assessmentId,
                    SiteCollectionId = site,
                    WebId = discovered.WebId,
                    FileUniqueId = file,
                    SiteUrl = web.SiteUrl,
                    WebUrl = web.WebUrl,
                    ListId = source.ListId,
                    ListItemId = discovered.ListItemId,
                    ListTitle = source.ListTitle,
                    ListUrl = source.ListUrl,
                    PageUrl = discovered.Url,
                    PageName = source.Fields.GetValueOrDefault("Title")?.Text ?? discovered.FileName,
                    PageType = ClassicPageRules.ModernPage,
                    AssessmentStatus = "NotApplicable",
                    DiscoveryStatus = discovered.DiscoveryStatus,
                    ClassificationReason = "ObservedModernPageItemMetadata",
                }
            };
        if (result.Page != null)
        {
            result.Page.Identity = PageIdentity.File(site, file);
            result.Page.SourceKind = "File";
            result.Page.AssessmentPageType = result.Page.PageType;
        }

        return result;
    }

    /// <summary>
    /// Applies readiness analysis only after native physical identity is established, retaining original discovery and failure provenance.
    /// </summary>
    private static FilePageAnalysis AnalyzeCapturedEvidence(
        Guid assessmentId,
        WebFacts web,
        DiscoveryEvidence discovered,
        FilePageEvidence source,
        ClassicCollectionOptions options,
        WebPartMappingManager mapping)
    {
        // The analysis mutates only a detached projection, never the source facts.
        DiscoveryEvidence? row = JsonSerializer.Deserialize<DiscoveryEvidence>(JsonSerializer.Serialize(discovered))!;
        row.HomePage = SnapshotHomePage.ResolveHomePageState(
            row.Url,
            web.WelcomePage,
            web.WelcomePageState.Succeeded,
            row.HomePage);
        if (options.HomePageOnly && row.HomePage != true)
        {
            row.AssessmentStatus = "NotSelected";
            return new(null, [], row, false);
        }

        if (source.FileLevelOnly)
        {
            FileLevelPageProjection.ListAssociationState association = FileLevelPageProjection.ClassifyListAssociation(
                row,
                source.OwningListMatched == true,
                source.ListRootFolderUrls);
            ClassicPageRow? filePage = FileLevelPageProjection.Project(row, association);
            if (filePage == null) return new(null, [], row, false);
            ClassicPageWebPartRow[] fileParts = [];
            if (source.WebPartsState.Succeeded)
            {
                fileParts = PageWebPartProjection.Build(filePage, source, options.ExportWebPartProperties).ToArray();
                if (fileParts.Length > 0) PageMappingCalculator.ApplyMapping(filePage, fileParts, mapping);
            }
            else
            {
                row.AssessmentStatus = "Failed";
                AddError(row, "WebPartAssessment", source.WebPartsState);
            }

            filePage.AssessmentStatus = row.AssessmentStatus;
            if (filePage.HomePage == true && web.CanModernizeState.Succeeded)
                filePage.UncustomizedHomePage = web.CanModernizeHomepage == true;
            return new(filePage, fileParts, row, false);
        }

        if (row.ListId == null || row.ListItemId == null)
        {
            row.AssessmentStatus = "NotApplicable";
            return new(null, [], row, false);
        }

        if (!source.MetadataState.Succeeded)
        {
            row.AssessmentStatus = "Failed";
            AddError(row, "PageMetadata", source.MetadataState);
            return new(null, [], row, false, source.MetadataState.Error ?? source.MetadataState.Status);
        }

        Dictionary<string, object?>? values = source.Fields.ToDictionary(x => x.Key, x => x.Value.ToValue());
        string? url = ClassicPageRules.ResolvePhysicalPageUrl(values, row.Url);
        string? contentType = source.Fields.GetValueOrDefault("ContentTypeId")?.Text ?? row.ContentTypeId;
        string type = ClassicPageRules.IsPublishingContentType(contentType)
            ? ClassicPageRules.PublishingPage
            : ClassicPageRules.GetPageType(values);
        row.PageType = type;
        row.AssessmentStatus = type == ClassicPageRules.ModernPage ? "NotApplicable" : "Complete";
        if (type == ClassicPageRules.ModernPage) return new(null, [], row, true);
        string? name = source.Fields.GetValueOrDefault("Title")?.Text;
        ClassicPageRow? page = new ClassicPageRow
        {
            ScanId = assessmentId,
            SiteUrl = web.SiteUrl,
            WebUrl = web.WebUrl,
            PageUrl = url,
            PageName = !string.IsNullOrEmpty(name) ? name : Path.GetFileNameWithoutExtension(url),
            ListId = source.ListId,
            ListTitle = source.ListTitle,
            ListUrl = source.ListUrl,
            ModifiedAt =
                source.Fields.TryGetValue("Modified", out SourceField? modified) && modified.ToValue() is DateTime date
                    ? date
                    : default,
            ModifiedBy = ModifiedBy(source.Fields, options.SkipUserInformation),
            PageType = type,
            HomePage = row.HomePage,
            SiteCollectionId = row.SiteCollectionId,
            WebId = row.WebId,
            FileUniqueId = row.FileUniqueId,
            ListItemId = row.ListItemId,
            DiscoveryStatus = row.DiscoveryStatus,
            AssessmentStatus = row.AssessmentStatus,
            CustomizedPageStatus = row.CustomizedPageStatus,
            LayoutUrl = SnapshotPageLayout.GetPublishingPageLayoutUrl(source.Fields),
            LayoutCustomizedPageStatus = source.PublishingLayout?.CustomizedPageStatus,
            RemediationCode = type switch
            {
                ClassicPageRules.WebPartPage => "CP1",
                ClassicPageRules.WikiPage => "CP2",
                ClassicPageRules.PublishingPage => "CP3",
                ClassicPageRules.ASPXPage => "CP5",
                _ => null
            },
        };
        ClassicPageWebPartRow[] parts = [];
        string? error = null;
        if (type is ClassicPageRules.WebPartPage or ClassicPageRules.WikiPage or ClassicPageRules.PublishingPage)
        {
            if (source.WebPartsState.Succeeded)
            {
                try
                {
                    parts = PageWebPartProjection.Build(page, source, options.ExportWebPartProperties).ToArray();
                    PageMappingCalculator.ApplyMapping(page, parts, mapping);
                    if (page.HomePage == true)
                    {
                        if (web.CanModernizeState.Succeeded)
                            page.UncustomizedHomePage = web.CanModernizeHomepage == true;
                        else if (web.HomeFallbackState.Succeeded && source.HomeFallbackState.Succeeded)
                            page.UncustomizedHomePage = HomeFallback(web, page, source, parts);
                        else
                            error = "Home page API and fallback were not available: " +
                                    (web.HomeFallbackState.Error ?? source.HomeFallbackState.Error);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    parts = [];
                    error = ex.GetType().Name + ": " + ex.Message;
                    row.AssessmentStatus = "Failed";
                    AddError(row, "WebPartAssessment", new("Failed", error));
                }
            }
            else
            {
                error = source.WebPartsState.Error ?? source.WebPartsState.Status;
                row.AssessmentStatus = "Failed";
                AddError(row, "WebPartAssessment", source.WebPartsState);
            }
        }

        page.AssessmentStatus = row.AssessmentStatus;
        return new(page, parts, row, false, error);
    }

    /// <summary>
    /// Evaluates the existing home-page fallback from saved features, layout, and part evidence.
    /// </summary>
    private static bool HomeFallback(
        WebFacts web,
        ClassicPageRow page,
        FilePageEvidence source,
        ClassicPageWebPartRow[] parts)
    {
        string[]? split = web.WebTemplate.Split('#');
        int configuration = split.Length == 2 && int.TryParse(split[1], out int parsed) ? parsed : -1;
        return HomePageDetector.IsUncustomizedHomePageFallback(
            true,
            split[0],
            configuration,
            web.SiteFeatures.Contains(ClassicPageRules.PublishingSiteFeature),
            web.WebFeatures.Contains(ClassicPageRules.PublishingWebFeature),
            web.WebFeatures.Contains(new Guid("F478D140-B148-4038-9CB0-84A8F1E4BE09")),
            web.WebFeatures.Contains(new Guid("E3DC7334-CEC0-4D2C-8B90-E4857698FC4E")),
            web.MasterUrl,
            source.Fields.GetValueOrDefault("FileLeafRef")?.Text,
            Regex.Replace(web.LocalizedHomePageResource ?? "", @"['´`]", "") + ".aspx",
            source.Fields.GetValueOrDefault("WikiField")?.Text,
            parts.Select(x => new WebPartEntity { Type = x.WebPartType }).ToArray(),
            source.ContentTypeDisplayFormTemplateName);
    }

    /// <summary>
    /// Reads the captured editor identity unless user information was excluded.
    /// </summary>
    internal static string? ModifiedBy(Dictionary<string, SourceField> fields, bool skipUserInformation = false)
    {
        if (skipUserInformation) return null;
        if (!fields.TryGetValue("Editor", out SourceField? field) || field.Type != "User" ||
            field.Value.ValueKind != JsonValueKind.Object)
            return null;
        string? email = field.Value.TryGetProperty("Email", out JsonElement e) ? e.GetString() : null;
        if (!string.IsNullOrEmpty(email)) return email;
        return field.Value.TryGetProperty("LookupValue", out JsonElement lookup) ? lookup.GetString() : null;
    }

    /// <summary>
    /// Appends distinct assessment stage and acquisition diagnostics to a detached discovery row.
    /// </summary>
    internal static void AddError(DiscoveryEvidence row, string stage, SourceReadState state)
    {
        row.ErrorStage = string.Join(
            ';',
            new[] { row.ErrorStage, stage }.Where(x => !string.IsNullOrEmpty(x)).Distinct());
        row.ErrorCodes = string.Join(
            ';',
            new[] { row.ErrorCodes, state.Status }.Where(x => !string.IsNullOrEmpty(x)).Distinct());
        row.ErrorDetail = string.Join(
            '\n',
            new[] { row.ErrorDetail, state.Error }.Where(x => !string.IsNullOrEmpty(x)).Distinct());
    }
}