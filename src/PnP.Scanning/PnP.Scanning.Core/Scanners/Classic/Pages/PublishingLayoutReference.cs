using Microsoft.EntityFrameworkCore;
using Microsoft.SharePoint.Client;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.Scanners;

/// <summary>
/// Reads only the content page's existing URL field. Resolution consumes the completed physical
/// inventory, never a target request, path-based asset classifier or layout-body analysis.
/// </summary>
internal static class PublishingLayoutReference
{
    internal const string FieldName = "PublishingPageLayout";
    private const string Pending = "InventoryPending";

    internal static void Capture(ClassicPage page, IDictionary<string, object> fields)
    {
        object value = fields != null && fields.TryGetValue(FieldName, out var field) ? field : null;
        string url = value switch
        {
            FieldUrlValue csom => csom.Url,
            PnP.Core.Model.SharePoint.IFieldUrlValue sdk => sdk.Url,
            _ => null,
        };
        var normalized = NormalizeUrl(url, page.SiteUrl);
        var reason = value == null ? "ReferenceMetadataMissing" :
            normalized == null ? "ReferenceMetadataUnusable" : Pending;

        // Repeated acquisition cannot erase earlier failure/conflicting-reference evidence.
        if (page.LayoutUrl != null && normalized != null &&
            !string.Equals(page.LayoutUrl, normalized, StringComparison.OrdinalIgnoreCase))
        {
            RetainUnavailable(page, "ReferenceMetadataConflict");
            return;
        }
        page.LayoutUrl ??= normalized;
        if (HasAcquisitionFailure(page)) return;
        page.LayoutReferenceStatus = "Unresolved";
        page.LayoutReferenceReason = reason;
    }

    internal static void RecordFailure(ClassicPage page, Exception error)
    {
        var denied = error is UnauthorizedAccessException ||
            error is ServerUnauthorizedAccessException ||
            error is ServerException { ServerErrorCode: -2147024891 };
        RetainUnavailable(page, denied ? "ReferenceMetadataDenied" : "ReferenceMetadataFailed");
        // The existing caller also retains the exception code/detail in discovery evidence.
    }

    internal static void RetainUnavailable(ClassicPage page, string reason)
    {
        page.LayoutReferenceStatus = "Unresolved";
        page.LayoutReferenceReason = AssessmentDiscoveryWriter.Join(
            page.LayoutReferenceReason is "NotEvaluated" or Pending ? null : page.LayoutReferenceReason, reason);
    }

    private static bool HasAcquisitionFailure(ClassicPage page) =>
        (page.LayoutReferenceReason ?? "").Split(';').Any(reason => reason is
            "ReferenceMetadataDenied" or "ReferenceMetadataFailed" or "ReferenceMetadataUnknown" or "ReferenceMetadataConflict");

    // Canonical escaped server-relative path. Do not infer a path from a name, web-relative value,
    // foreign host or ambiguous URL. Apply exactly the same normalization to inventory locators.
    internal static string NormalizeUrl(string value, string siteUrl)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(siteUrl, UriKind.Absolute, out var site) ||
            site.Scheme is not ("https" or "http")) return null;
        value = value.Trim();
        if (value.Contains('\\') || value.Contains('?') || value.Contains('#') ||
            value.Any(char.IsControl) || value.StartsWith("//", StringComparison.Ordinal)) return null;
        // Invalid escapes and encoded separators are not evidence of an unambiguous file identity.
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != '%') continue;
            if (i + 2 >= value.Length || !Uri.IsHexDigit(value[i + 1]) || !Uri.IsHexDigit(value[i + 2])) return null;
            var escape = value.Substring(i + 1, 2);
            if (escape.Equals("2f", StringComparison.OrdinalIgnoreCase) ||
                escape.Equals("5c", StringComparison.OrdinalIgnoreCase)) return null;
            i += 2;
        }
        Uri target;
        if (value.StartsWith('/'))
        {
            if (!Uri.TryCreate(site.GetLeftPart(UriPartial.Authority) + value, UriKind.Absolute, out target)) return null;
        }
        else if (!Uri.TryCreate(value, UriKind.Absolute, out target)) return null;
        if (!string.Equals(target.Scheme, site.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(target.Authority, site.Authority, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(target.UserInfo)) return null;
        var path = target.AbsolutePath;
        if (path.EndsWith('/') || path.Contains("//", StringComparison.Ordinal)) return null;
        var segments = path.Split('/').Select(Uri.UnescapeDataString).ToArray();
        if (segments.Any(segment => segment.Any(char.IsControl))) return null;
        return string.Join('/', segments.Select(Uri.EscapeDataString));
    }

    internal static async Task FinalizeAsync(ScanContext db, Guid scanId,
        IReadOnlyList<ClassicPageDiscovery> inventory, CancellationToken cancellationToken)
    {
        // InventoryPending also exists in historical scans. The stored authority, not the
        // reference state or this executable's version, permits corrected finalization.
        var ruleVersion = await AssessmentDiscoveryWriter.ReadRuleVersionAsync(db, scanId, cancellationToken).ConfigureAwait(false);
        if (ruleVersion != PublishingLayoutTypeCatalog.CurrentRuleVersion) return;

        var pages = await db.ClassicPages.Where(page => page.ScanId == scanId &&
            page.PageType == PageScanComponent.PublishingPage && page.LayoutReferenceStatus == "Unresolved" &&
            page.LayoutReferenceReason == Pending).ToListAsync(cancellationToken).ConfigureAwait(false);
        var targets = inventory.Where(row => row.ScanId == scanId && row.RowType == "Page")
            .ToLookup(row => Key(row.SiteUrl, NormalizeUrl(row.Url, row.SiteUrl)), StringComparer.OrdinalIgnoreCase);
        foreach (var page in pages)
        {
            var url = NormalizeUrl(page.LayoutUrl, page.SiteUrl);
            if (url == null)
            {
                page.LayoutReferenceReason = page.LayoutUrl == null ? "ReferenceMetadataMissing" : "ReferenceMetadataUnusable";
                continue;
            }
            var matches = targets[Key(page.SiteUrl, url)].Where(row =>
                !page.SiteCollectionId.HasValue || !row.SiteCollectionId.HasValue ||
                page.SiteCollectionId == row.SiteCollectionId).ToArray();
            var reason = matches.Length == 0 ? "TargetNotDiscovered" :
                matches.Length != 1 ? "TargetAmbiguous" : TargetReason(matches[0]);
            page.LayoutReferenceStatus = reason == "ConfirmedPublishingLayoutFamily" ? "Resolved" : "Unresolved";
            page.LayoutReferenceReason = reason;
        }
        // The caller saves this together with scan finalization, after every Web worker has finished.
    }

    private static string Key(string siteUrl, string url) => (siteUrl ?? "").TrimEnd('/') + "\n" + url;

    private static string TargetReason(ClassicPageDiscovery row)
    {
        if (row.DiscoveryStatus is "Denied" or "Failed" or "Unknown") return "TargetDiscovery" + row.DiscoveryStatus;
        if (row.AssessmentStatus is "Denied" or "Failed" or "Unknown") return "TargetAssessment" + row.AssessmentStatus;
        if (row.DiscoveryStatus != "Discovered") return "TargetDiscoveryUnavailable";
        if (row.PageTypeSourceStatus is "Denied" or "Failed" or "Unknown") return "TargetTypeSource" + row.PageTypeSourceStatus;
        if (row.PageTypeSourceStatus != "Available") return "TargetTypeSourceUnavailable";
        if (PublishingLayoutTypeEvidence.IsConfirmedMember(row)) return "ConfirmedPublishingLayoutFamily";
        if (row.PageTypeEvidenceOrigin != "DeclaredSource") return "TargetTypeEvidenceUnavailable";
        return row.PageTypeResolutionStatus == "Resolved" && row.PublishingLayoutFamily == "NonMember"
            ? "TargetNotPublishingLayoutFamily" : "TargetTypeFamilyUnknown";
    }
}
