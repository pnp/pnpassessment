#nullable enable
using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.Discovery.Aspx;

/// <summary>Preserves the durable identities and retained receipts of previously saved preview evidence.</summary>
internal static class ClassicAspxEvidenceCompatibility
{
    private const string LegacyRecordPrefix = "m1:";
    private const string PaginationRecordType = "Pagination";
    private const string UnknownState = "Unknown";
    private const string ChangedDuringScan = "ChangedDuringScan";
    private const string RetainedReceiptDetail = "The previously saved pagination receipt was retained after acquisition naming changed.";

    /// <summary>Returns the earlier key spelling only for a current ASPX evidence key, never for a normalized resource identity.</summary>
    internal static string? LegacyKeyFor(string? recordKey)
    {
        return recordKey != null && recordKey.StartsWith(ClassicAspxAcquisitionEvidenceNames.RecordPrefix, StringComparison.Ordinal)
            ? LegacyRecordPrefix + recordKey[ClassicAspxAcquisitionEvidenceNames.RecordPrefix.Length..]
            : null;
    }

    /// <summary>
    /// Reuses an existing preview row's exact key and parent rather than inserting a renamed duplicate during resume.
    /// Different incoming pagination evidence stays uncertain while the previously retained receipt bytes remain intact.
    /// </summary>
    internal static void PreserveIdentity(ClassicPageDiscovery incoming, ClassicPageDiscovery previous)
    {
        incoming.RecordKey = previous.RecordKey;
        incoming.ParentScopeKey = previous.ParentScopeKey;
        if (previous.RowType == PaginationRecordType && previous.EvidenceJson != incoming.EvidenceJson)
        {
            incoming.DiscoveryStatus = UnknownState;
            incoming.EvidenceJson = previous.EvidenceJson;
            AssessmentWebDiscovery.AddError(incoming, PaginationRecordType, ChangedDuringScan, RetainedReceiptDetail);
        }
    }
}
