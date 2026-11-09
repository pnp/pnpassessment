using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Pipeline.Contracts.Page;

namespace PnP.Scanning.Core.Pipeline.Analysis.Page;

/// <summary>
/// Assesses physical pages whose list-item association is absent or unresolved.
/// </summary>
internal static class FileLevelPageProjection
{
    internal const string ListAssociationAbsentCode = "list_association_absent";
    internal const string ListItemAbsentCode = "list_item_absent";
    internal const string OwningListUnresolvedCode = "owning_list_unresolved";
    internal const string ListAssociationUnknownCode = "list_association_unknown";
    internal const string SystemPageTemplateCode = "system_page_template";
    /// <summary>
    /// Distinguishes confirmed associations from unmatched owners and unknown list scope.
    /// </summary>
    internal enum ListAssociationState { Resolved, OwningListUnresolved, Unknown }

    /// <summary>
    /// Distinguishes a matched list, an unmatched named owner, and an unknown list association.
    /// </summary>
    internal static ListAssociationState ClassifyListAssociation(ClassicPageDiscoveryRow row,
        bool listMatched, IEnumerable<string> listRootFolderUrls) =>
        row.ListId != null ? listMatched ? ListAssociationState.Resolved : ListAssociationState.OwningListUnresolved
        : IsInsideAnyListRootFolder(row.Url, listRootFolderUrls) ? ListAssociationState.Unknown : ListAssociationState.Resolved;

    /// <summary>
    /// Checks whether an observed path lies beneath any captured list root.
    /// </summary>
    internal static bool IsInsideAnyListRootFolder(string url, IEnumerable<string> roots)
    {
        if (string.IsNullOrWhiteSpace(url) || roots == null) return false;
        string path = url.TrimEnd('/');
        return roots.Where(root => !string.IsNullOrWhiteSpace(root)).Select(root => root.TrimEnd('/'))
            .Any(root => root.Length > 0 && path.Length > root.Length && path[root.Length] == '/' &&
                path.StartsWith(root, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Maps the observed setup-template family without admitting registered list forms or views.
    /// </summary>
    internal static string FileLevelPageType(SetupPathPageFamily family) => family switch
    {
        SetupPathPageFamily.ListView or SetupPathPageFamily.ListForm => null,
        SetupPathPageFamily.WebPartPage => ClassicPageRules.WebPartPage,
        _ => ClassicPageRules.ASPXPage,
    };

    /// <summary>
    /// Builds a file-level page result while preserving missing-association diagnostics.
    /// </summary>
    internal static ClassicPageRow Project(ClassicPageDiscoveryRow row, ListAssociationState association)
    {
        void Error(string code, string detail)
        {
            row.ErrorStage = Join(row.ErrorStage, "PageItemMetadata");
            row.ErrorCodes = Join(row.ErrorCodes, code);
            row.ErrorDetail = Join(row.ErrorDetail, detail, "\n");
        }

        static string Join(string left, string right, string separator = ";") =>
            string.Join(separator, new[] { left, right }.Where(x => !string.IsNullOrWhiteSpace(x))
                .SelectMany(x => separator == "\n" ? new[] { x } : x.Split(separator, StringSplitOptions.RemoveEmptyEntries))
                .Distinct(StringComparer.Ordinal));
        if (association == ListAssociationState.OwningListUnresolved)
            Error(OwningListUnresolvedCode, "The file names an owning list that was not matched in this web.");
        else if (association == ListAssociationState.Unknown)
            Error(ListAssociationUnknownCode, "The file lies inside a loaded list, but its list association was not observed.");
        else if (row.ListId == null)
            Error(ListAssociationAbsentCode, "The file is not list-backed; no list-item surface exists to read.");
        if (row.ListItemId == null)
            Error(ListItemAbsentCode, "The file has no backing list item; item fields and WikiField are not applicable.");
        SetupPathPageFamily family = SetupPathClassifier.Classify(row.SetupPath);
        string type = FileLevelPageType(family);
        if (type == null)
        {
            Error(SystemPageTemplateCode, $"The file ghosts from the stock {family} template '{row.SetupPath}'.");
            row.AssessmentStatus = "NotApplicable";
            return null;
        }
        if (string.IsNullOrWhiteSpace(row.Url) || !row.Url.StartsWith('/'))
        {
            Error(DiscoveryGapCodes.IdentityMissing, "The discovered file has no server-relative URL.");
            row.AssessmentStatus = "Failed";
            return null;
        }
        row.PageType = type;
        row.AssessmentStatus = association == ListAssociationState.Resolved ? "Complete" : "Failed";
        return new ClassicPageRow
        {
            ScanId = row.ScanId, SiteUrl = row.SiteUrl, WebUrl = row.WebUrl, PageUrl = row.Url,
            PageName = Path.GetFileNameWithoutExtension(string.IsNullOrEmpty(row.FileName) ? row.Url : row.FileName),
            SiteCollectionId = row.SiteCollectionId, WebId = row.WebId, FileUniqueId = row.FileUniqueId,
            PageType = type, HomePage = row.HomePage, CustomizedPageStatus = row.CustomizedPageStatus,
            RemediationCode = type == ClassicPageRules.WebPartPage ? "CP1" : "CP5",
            DiscoveryStatus = row.DiscoveryStatus, AssessmentStatus = row.AssessmentStatus
        };
    }
}
