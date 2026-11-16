#nullable enable
using PnP.Scanning.Core.ClassicPages.Contracts.Page;
using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.ClassicPages.Compatibility;

/// <summary>
/// Names one native Page that the URL-keyed report rows leave out, with the reason, so one such Page no longer stops
/// the assessment or its export. The native publication keeps the Page; this row is the report's record of it.
/// </summary>
internal sealed record ClassicPageOmission(
    Guid ScanId,
    string? SiteUrl,
    string? WebUrl,
    string? PageUrl,
    string? PageName,
    string? PageType,
    string ObjectKind,
    string ContentKey,
    Guid? ListId,
    int? ListItemId,
    Guid? FileUniqueId,
    string Reason,
    string? SharesAddressWith)
{
    /// <summary>The Page has no address the report can show.</summary>
    internal const string NoAddress = "NoAddress";

    /// <summary>Another Page, named in SharesAddressWith, holds the same site, web and address and keeps the row.</summary>
    internal const string SharedAddress = "SharedAddress";

    /// <summary>The file meets the Pages selection and has no analyzed Page to report.</summary>
    internal const string NotAnalyzed = "NotAnalyzed";

    /// <summary>Records an analyzed Page that the legacy report table cannot carry.</summary>
    internal static ClassicPageOmission Of(
        ClassicPageRow page,
        PageIdentity identity,
        string reason,
        PageIdentity? owner = null) =>
        new(
            page.ScanId,
            page.SiteUrl,
            page.WebUrl,
            page.PageUrl,
            page.PageName,
            page.PageType,
            identity.ObjectKind.ToString(),
            identity.Key,
            page.ListId,
            page.ListItemId,
            page.FileUniqueId,
            reason,
            owner?.Key);

    /// <summary>Records a selected Page that the exported Pages CSV cannot carry.</summary>
    internal static ClassicPageOmission Of(ClassicPageCsvRow row, string reason, PageIdentity? owner = null) =>
        new(
            row.ScanId,
            row.SiteUrl,
            row.WebUrl,
            row.PageUrl,
            row.PageName,
            row.PageType,
            row.ObjectKind,
            row.ContentKey,
            row.ListId,
            row.ListItemId,
            row.FileUniqueId,
            reason,
            owner?.Key);

    /// <summary>Records a selected physical file that has no analyzed Page.</summary>
    internal static ClassicPageOmission Of(SPFile file, PageIdentity identity, string pageType) =>
        new(
            file.ScanId,
            file.SiteUrl,
            file.WebUrl,
            file.ServerRelativeUrl,
            file.FileName,
            pageType,
            identity.ObjectKind.ToString(),
            identity.Key,
            file.ListId,
            file.ListItemId,
            file.FileUniqueId,
            NotAnalyzed,
            null);
}