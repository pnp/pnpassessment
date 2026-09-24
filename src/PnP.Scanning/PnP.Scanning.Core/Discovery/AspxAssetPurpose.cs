using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.Discovery;

/// <summary>
/// File purpose is independent of page-family/runtime evidence. Only content type proves a layout;
/// neither the file's location nor a missing field establishes an asset or a content page.
/// </summary>
internal sealed record AspxAssetPurpose(string Purpose, string Status, string Reason)
{
    internal const string LayoutContentType = "0x01010007FF3E057FA8AB4AA42FCB67B453FFC1";
    internal const string PublishingContentType = "0x010100C568DB52D9D0A14D9B2FDCC96666E9F2007948130EC3DB064584E219954237AF39";

    internal static bool IsPublishingContentType(string contentTypeId) => IsFamily(contentTypeId, PublishingContentType);

    internal static AspxAssetPurpose Decide(string contentTypeId)
    {
        if (string.IsNullOrWhiteSpace(contentTypeId)) return new("Unknown", "Unknown", "ContentTypeUnavailable");
        if (IsFamily(contentTypeId, LayoutContentType)) return new("PageLayout", "Confirmed", "PageLayoutContentType");
        if (IsPublishingContentType(contentTypeId) ||
            IsFamily(contentTypeId, "0x0101009D1CB255DA76424F860D91F20E6C4118") ||
            IsFamily(contentTypeId, "0x010109") || IsFamily(contentTypeId, "0x010108"))
            return new("ContentPage", "Confirmed", "ContentPageContentType");
        return new("Unknown", "Unknown", "UnrecognizedContentType");
    }

    private static bool IsFamily(string id, string parent) =>
        !string.IsNullOrWhiteSpace(id) && id.StartsWith(parent, StringComparison.OrdinalIgnoreCase);

    internal static bool IsLayout(ClassicPageDiscovery row) =>
        row.AssetPurpose == "PageLayout" && row.AssetPurposeStatus == "Confirmed";

    internal static void Apply(ClassicPageDiscovery row, string contentTypeId)
    {
        var decision = Decide(contentTypeId);
        // An observation without metadata cannot erase a confirmed purpose. A layout observed
        // anywhere in this scan remains excluded even if a later observation changes its type.
        if (!IsLayout(row) && (decision.Status == "Confirmed" || row.AssetPurposeStatus != "Confirmed"))
        {
            row.AssetPurpose = decision.Purpose;
            row.AssetPurposeStatus = decision.Status;
        }
        row.AssetPurposeReason = MergeReasons(row.AssetPurposeReason, decision.Reason);
    }

    internal static void Merge(ClassicPageDiscovery previous, ClassicPageDiscovery row)
    {
        if (IsLayout(previous) || (previous.AssetPurposeStatus == "Confirmed" && row.AssetPurposeStatus != "Confirmed"))
        {
            row.AssetPurpose = previous.AssetPurpose;
            row.AssetPurposeStatus = previous.AssetPurposeStatus;
        }
        row.AssetPurposeReason = MergeReasons(previous.AssetPurposeReason, row.AssetPurposeReason);
    }

    private static string MergeReasons(string left, string right)
    {
        var reasons = AssessmentDiscoveryWriter.Join(
            left == "NotEvaluated" ? null : left, right == "NotEvaluated" ? null : right);
        return string.IsNullOrEmpty(reasons) ? "NotEvaluated" : reasons;
    }
}
