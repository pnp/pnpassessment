using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Discovery;

#nullable enable

namespace PnP.Scanning.Core.Pipeline.Analysis.Page;

/// <summary>
/// Describes page taxonomy, retirement impact, and the evidence supporting that verdict.
/// </summary>
internal sealed record AspxPageClassification(
    string PageType,
    string ProductFamily,
    string Experience,
    string ImpactStatus,
    string? ImpactedBy,
    string Reason);

/// <summary>
/// Classifies exact handlers using independent list-role and catalog evidence.
/// </summary>
internal static class AspxPageClassifier
{
    internal const string RuleVersion = "aspx-page-policy/v4";
    internal const string MessageCenterUrl = "https://msmessagecenter.com/MC1464926/";
    private const string WebPartPageHandler = "Microsoft.SharePoint.WebPartPages.WebPartPage";
    private const string DeprecatedMobileHandler = "Microsoft.SharePoint.MobileControls.SPMobilePage";

    private static readonly string[] CatalogRoots =
    [
        "masterpage", "users", "wp", "lt", "wt", "theme", "design", "solutions", "appdata", "appfiles",
    ];

    private static readonly HashSet<string> ModernHandlers = new(StringComparer.Ordinal)
    {
        "Microsoft.SharePoint.WebControls.ClientSidePage",
        "Microsoft.SharePoint.WebControls.ClientSideFormPage",
        "Microsoft.SharePoint.WebControls.ClientSideLayoutsPage",
        "Microsoft.SharePoint.WebControls.SecuredClientSideLayoutsPage",
        "Microsoft.SharePoint.WebControls.WikiLayoutsPage",
    };

    /// <summary>
    /// Derives page taxonomy and impact only from exact handlers and observed role evidence.
    /// </summary>
    internal static AspxPageClassification Classify(
        string? handler,
        int? listTemplate,
        bool? listView,
        bool? listForm,
        bool? catalog,
        string? webRelativePath = null)
    {
        string? type = handler switch
        {
            "Microsoft.SharePoint.WebPartPages.WikiEditPage" => ClassicPageRules.WikiPage,
            WebPartPageHandler when listTemplate == 301 => ClassicPageRules.BlogPage,
            WebPartPageHandler => ClassicPageRules.WebPartPage,
            "Microsoft.SharePoint.Publishing.TemplateRedirectionPage" => ClassicPageRules.PublishingPage,
            AspxSourceParser.DefaultHandler => ClassicPageRules.ASPXPage,
            { } modern when ModernHandlers.Contains(modern) => ClassicPageRules.ModernPage,
            null => "Unknown",
            _ => "Other",
        };
        if (listView == true || listForm == true)
            return new(type, "List", "Inapplicable", "Excluded", null, "RegisteredListViewOrForm");
        if (catalog == true)
        {
            bool modern = handler != null && ModernHandlers.Contains(handler);
            bool exactClassic = type is ClassicPageRules.WikiPage or ClassicPageRules.WebPartPage
                or ClassicPageRules.PublishingPage or ClassicPageRules.ASPXPage or ClassicPageRules.BlogPage;
            return new(
                type,
                CatalogFamily(webRelativePath),
                modern ? "Modern" : "Classic",
                "Excluded",
                null,
                modern
                    ? "CatalogAsset;ExactModernHandler"
                    : exactClassic
                        ? "CatalogAsset;ExactClassicHandler"
                        : "CatalogAsset;ClassicDefaultAssumed");
        }

        if (handler == null) return new(type, "Unknown", "Unknown", "Unknown", null, "HandlerUnavailable");
        if (listView == null || listForm == null || catalog == null)
            return new(type, "Unknown", "Unknown", "Unknown", null, "RoleEvidenceIncomplete");
        if (handler == DeprecatedMobileHandler)
            return new(type, "deprecated", "Inapplicable", "Excluded", null, "DeprecatedHandler");
        if (ModernHandlers.Contains(handler))
            return new(type, "User", "Modern", "Excluded", null, "ExactModernHandler");
        if (type is "Unknown" or "Other") return new(type, "Unknown", "Unknown", "Unknown", null, "UnmappedHandler");
        return new(type, "User", "Classic", "Included", MessageCenterUrl, "ClassicUserPage;MC1464926");
    }

    /// <summary>
    /// Maps an observed Web-relative catalog root to its specific family or catalog_other.
    /// </summary>
    private static string CatalogFamily(string? webRelativePath)
    {
        if (webRelativePath == null) return "catalog_other";
        foreach (string? root in CatalogRoots)
            if (webRelativePath.StartsWith("_catalogs/" + root + "/", StringComparison.OrdinalIgnoreCase))
                return "catalog_" + root;
        if (webRelativePath.StartsWith("Style Library/", StringComparison.OrdinalIgnoreCase))
            return "catalog_style_library";
        return "catalog_other";
    }
}