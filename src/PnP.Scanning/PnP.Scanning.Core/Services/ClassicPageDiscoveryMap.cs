using CsvHelper.Configuration;
using PnP.Scanning.Core.Storage;
using System.Globalization;

namespace PnP.Scanning.Core.Services;

/// <summary>Preserve the original native header order and append the CP1 evidence columns.</summary>
internal sealed class ClassicPageDiscoveryMap : ClassMap<ClassicPageDiscovery>
{
    internal ClassicPageDiscoveryMap(CultureInfo culture)
    {
        AutoMap(culture);
        var original = new[]
        {
            nameof(ClassicPageDiscovery.RecordKey), nameof(ClassicPageDiscovery.RowType),
            nameof(ClassicPageDiscovery.ScopeType), nameof(ClassicPageDiscovery.ParentScopeKey),
            nameof(ClassicPageDiscovery.Url), nameof(ClassicPageDiscovery.SiteCollectionId),
            nameof(ClassicPageDiscovery.WebId), nameof(ClassicPageDiscovery.ListId),
            nameof(ClassicPageDiscovery.FolderUniqueId), nameof(ClassicPageDiscovery.FileUniqueId),
            nameof(ClassicPageDiscovery.ListItemId), nameof(ClassicPageDiscovery.FileName),
            nameof(ClassicPageDiscovery.AssetPurpose), nameof(ClassicPageDiscovery.AssetPurposeStatus),
            nameof(ClassicPageDiscovery.AssetPurposeReason), nameof(ClassicPageDiscovery.ContentTypeId),
            nameof(ClassicPageDiscovery.DeclaredPageType), nameof(ClassicPageDiscovery.ResolvedPageType),
            nameof(ClassicPageDiscovery.PageTypeEvidenceOrigin), nameof(ClassicPageDiscovery.PageTypeSourceStatus),
            nameof(ClassicPageDiscovery.PageTypeResolutionStatus), nameof(ClassicPageDiscovery.PublishingLayoutFamily),
            nameof(ClassicPageDiscovery.PageTypeReason), nameof(ClassicPageDiscovery.PageTypeEvidenceJson),
            nameof(ClassicPageDiscovery.HomePage), nameof(ClassicPageDiscovery.LibraryHidden),
            nameof(ClassicPageDiscovery.ObservationMethod), nameof(ClassicPageDiscovery.DiscoveryStatus),
            nameof(ClassicPageDiscovery.AssessmentStatus), nameof(ClassicPageDiscovery.ExpectedChildCount),
            nameof(ClassicPageDiscovery.ObservedChildCount), nameof(ClassicPageDiscovery.ErrorStage),
            nameof(ClassicPageDiscovery.ErrorCodes), nameof(ClassicPageDiscovery.ErrorDetail),
            nameof(ClassicPageDiscovery.EvidenceJson), nameof(ClassicPageDiscovery.ObservedAtUtc),
            nameof(ClassicPageDiscovery.ScanId), nameof(ClassicPageDiscovery.SiteUrl), nameof(ClassicPageDiscovery.WebUrl),
        };
        var index = 0;
        foreach (var name in original) MemberMaps.Single(map => map.Data.Member.Name == name).Index(index++);
        foreach (var map in MemberMaps.Where(map => !original.Contains(map.Data.Member.Name, StringComparer.Ordinal)))
            map.Index(index++);
    }
}
