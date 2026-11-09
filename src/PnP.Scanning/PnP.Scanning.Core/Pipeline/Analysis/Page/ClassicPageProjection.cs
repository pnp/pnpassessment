using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Discovery;
#nullable enable
using ClassicPage = PnP.Scanning.Core.Pipeline.Contracts.Page.ClassicPageRow;
using ClassicPageDiscovery = PnP.Scanning.Core.Pipeline.Contracts.Page.ClassicPageDiscoveryRow;
using ClassicPageWebPart = PnP.Scanning.Core.Pipeline.Contracts.Page.ClassicPageWebPartRow;
using System.Text.Json;
using System.Text.RegularExpressions;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Page;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Pipeline.Contracts.Web;
using PnP.Scanning.Core.Scanners.WebPartMapping;


namespace PnP.Scanning.Core.Pipeline.Analysis.Page;

/// <summary>
/// Carries an assessed page, web parts, discovery disposition, and explicit failures.
/// </summary>
internal sealed record ClassicPageProjectionResult(ClassicPage? Page, ClassicPageWebPart[] Parts,
    ClassicPageDiscovery? Discovery, bool Modern, string? Error = null);

/// <summary>
/// Applies page readiness rules to detached evidence without modifying collected source.
/// </summary>
internal static class ClassicPageProjection
{
    /// <summary>
    /// Assesses captured page fields and web parts while retaining nullable selection and failure provenance.
    /// </summary>
    internal static ClassicPageProjectionResult Analyze(Guid assessmentId, ClassicPageWebSource web,
        ClassicPageDiscovery discovered, ClassicPageItemSource source, ClassicPageSourceOptions options, WebPartMappingManager mapping)
    {
        // The analysis mutates only a detached projection, never the source facts.
        ClassicPageDiscovery? row = JsonSerializer.Deserialize<ClassicPageDiscovery>(JsonSerializer.Serialize(discovered))!;
        row.HomePage = SnapshotHomePage.ResolveHomePageState(row.Url, web.WelcomePage, web.WelcomePageState.Succeeded, row.HomePage);
        if (options.HomePageOnly && row.HomePage != true)
        { row.AssessmentStatus = "NotSelected"; return new(null, [], row, false); }
        if (source.FileLevelOnly)
        {
            FileLevelPageProjection.ListAssociationState association = FileLevelPageProjection.ClassifyListAssociation(row,
                source.OwningListMatched == true, source.ListRootFolderUrls);
            ClassicPage? filePage = FileLevelPageProjection.Project(row, association);
            if (filePage == null) return new(null, [], row, false);
            ClassicPageWebPart[] fileParts = [];
            if (source.WebPartsState.Succeeded)
            {
                fileParts = PageWebPartExtractor.Build(filePage, source, options.ExportWebPartProperties).ToArray();
                if (fileParts.Length > 0) SnapshotPageMappingCalculator.ApplyMapping(filePage, fileParts, mapping);
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
        { row.AssessmentStatus = "NotApplicable"; return new(null, [], row, false); }
        if (!source.MetadataState.Succeeded)
        {
            row.AssessmentStatus = "Failed";
            AddError(row, "PageMetadata", source.MetadataState);
            return new(null, [], row, false, source.MetadataState.Error ?? source.MetadataState.Status);
        }
        Dictionary<string, object?>? values = source.Fields.ToDictionary(x => x.Key, x => x.Value.ToValue());
        string? url = ClassicPageRules.ResolvePhysicalPageUrl(values, row.Url);
        string? contentType = source.Fields.GetValueOrDefault("ContentTypeId")?.Text ?? row.ContentTypeId;
        string? type = ClassicPageRules.IsPublishingContentType(contentType)
            ? ClassicPageRules.PublishingPage : ClassicPageRules.GetPageType(values);
        row.PageType = type;
        row.AssessmentStatus = type == ClassicPageRules.ModernPage ? "NotApplicable" : "Complete";
        if (type == ClassicPageRules.ModernPage) return new(null, [], row, true);
        string? name = source.Fields.GetValueOrDefault("Title")?.Text;
        ClassicPage? page = new ClassicPage
        {
            ScanId = assessmentId, SiteUrl = web.SiteUrl, WebUrl = web.WebUrl, PageUrl = url,
            PageName = !string.IsNullOrEmpty(name) ? name : Path.GetFileNameWithoutExtension(url),
            ListId = source.ListId ?? Guid.Empty, ListTitle = source.ListTitle, ListUrl = source.ListUrl,
            ModifiedAt = source.Fields.TryGetValue("Modified", out SourceField? modified) && modified.ToValue() is DateTime date ? date : default,
            ModifiedBy = ModifiedBy(source.Fields, options.SkipUserInformation), PageType = type, HomePage = row.HomePage,
            SiteCollectionId = row.SiteCollectionId, WebId = row.WebId, FileUniqueId = row.FileUniqueId, ListItemId = row.ListItemId,
            DiscoveryStatus = row.DiscoveryStatus, AssessmentStatus = row.AssessmentStatus,
            CustomizedPageStatus = row.CustomizedPageStatus,
            LayoutUrl = SnapshotPageLayout.GetPublishingPageLayoutUrl(source.Fields),
            LayoutCustomizedPageStatus = source.PublishingLayout?.CustomizedPageStatus,
            RemediationCode = type switch { ClassicPageRules.WebPartPage => "CP1", ClassicPageRules.WikiPage => "CP2",
                ClassicPageRules.PublishingPage => "CP3", ClassicPageRules.ASPXPage => "CP5", _ => null },
        };
        ClassicPageWebPart[] parts = [];
        string? error = null;
        if (type is ClassicPageRules.WebPartPage or ClassicPageRules.WikiPage or ClassicPageRules.PublishingPage)
        {
            if (source.WebPartsState.Succeeded)
            {
                try
                {
                    parts = PageWebPartExtractor.Build(page, source, options.ExportWebPartProperties).ToArray();
                    SnapshotPageMappingCalculator.ApplyMapping(page, parts, mapping);
                    if (page.HomePage == true)
                    {
                        if (web.CanModernizeState.Succeeded) page.UncustomizedHomePage = web.CanModernizeHomepage == true;
                        else if (web.HomeFallbackState.Succeeded && source.HomeFallbackState.Succeeded)
                            page.UncustomizedHomePage = HomeFallback(web, page, source, parts);
                        else error = "Home page API and fallback were not available: " + (web.HomeFallbackState.Error ?? source.HomeFallbackState.Error);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    parts = []; error = ex.GetType().Name + ": " + ex.Message;
                    row.AssessmentStatus = "Failed"; AddError(row, "WebPartAssessment", new("Failed", error));
                }
            }
            else
            {
                error = source.WebPartsState.Error ?? source.WebPartsState.Status;
                row.AssessmentStatus = "Failed"; AddError(row, "WebPartAssessment", source.WebPartsState);
            }
        }
        page.AssessmentStatus = row.AssessmentStatus;
        return new(page, parts, row, false, error);
    }

    /// <summary>
    /// Evaluates the existing home-page fallback from saved features, layout, and part evidence.
    /// </summary>
    private static bool HomeFallback(ClassicPageWebSource web, ClassicPage page, ClassicPageItemSource source, ClassicPageWebPart[] parts)
    {
        string[]? split = web.Template.Split('#');
        int configuration = split.Length == 2 && int.TryParse(split[1], out int parsed) ? parsed : -1;
        return HomePageDetector.IsUncustomizedHomePageFallback(true, split[0], configuration,
            web.SiteFeatures.Contains(new Guid("F6924D36-2FA8-4F0B-B16D-06B7250180FA")),
            web.WebFeatures.Contains(new Guid("94C94CA6-B32F-4DA9-A9E3-1F3D343D7ECB")),
            web.WebFeatures.Contains(new Guid("F478D140-B148-4038-9CB0-84A8F1E4BE09")),
            web.WebFeatures.Contains(new Guid("E3DC7334-CEC0-4D2C-8B90-E4857698FC4E")),
            web.MasterUrl, source.Fields.GetValueOrDefault("FileLeafRef")?.Text,
            Regex.Replace(web.LocalizedHomePageResource ?? "", @"['´`]", "") + ".aspx",
            source.Fields.GetValueOrDefault("WikiField")?.Text,
            parts.Select(x => new WebPartEntity { Type = x.WebPartType }).ToArray(), source.ContentTypeDisplayFormTemplateName);
    }
    /// <summary>
    /// Reads the captured editor identity unless user information was excluded.
    /// </summary>
    internal static string? ModifiedBy(Dictionary<string, SourceField> fields, bool skipUserInformation = false)
    {
        if (skipUserInformation) return null;
        if (!fields.TryGetValue("Editor", out SourceField? field) || field.Type != "User" || field.Value.ValueKind != JsonValueKind.Object) return null;
        string? email = field.Value.TryGetProperty("Email", out JsonElement e) ? e.GetString() : null;
        return !string.IsNullOrEmpty(email) ? email : field.Value.TryGetProperty("LookupValue", out JsonElement lookup) ? lookup.GetString() : null;
    }
    /// <summary>
    /// Appends distinct assessment stage and acquisition diagnostics to a detached discovery row.
    /// </summary>
    internal static void AddError(ClassicPageDiscovery row, string stage, SourceReadState state)
    {
        row.ErrorStage = string.Join(';', new[] { row.ErrorStage, stage }.Where(x => !string.IsNullOrEmpty(x)).Distinct());
        row.ErrorCodes = string.Join(';', new[] { row.ErrorCodes, state.Status }.Where(x => !string.IsNullOrEmpty(x)).Distinct());
        row.ErrorDetail = string.Join('\n', new[] { row.ErrorDetail, state.Error }.Where(x => !string.IsNullOrEmpty(x)).Distinct());
    }
}
