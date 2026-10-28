namespace PnP.Scanning.Core.Storage;

/// <summary>Persisted coverage labels for physical ASPX observations; values remain part of the report contract.</summary>
internal static class ClassicAspxCoverageStates
{
    internal const string Unknown = "Unknown";
    internal const string Observed = "Observed";
    internal const string Incomplete = "Incomplete";
    internal const string Complete = "Complete";
    internal const string ChangedDuringScan = "ChangedDuringScan";
    internal const string VersionedReference = "VersionedReference";
}

/// <summary>Persisted read outcomes shared by file, handler, and item-identity observations.</summary>
internal static class ClassicAspxReadStates
{
    internal const string NotAttempted = "NotAttempted";
    internal const string NotRequested = "NotRequested";
    internal const string NotApplicable = "NotApplicable";
    internal const string Unavailable = "Unavailable";
    internal const string Available = "Available";
}

/// <summary>Evidence origins that distinguish actual file bytes from a frozen catalog declaration.</summary>
internal static class ClassicAspxHandlerSources
{
    internal const string ActualFileBytes = "ActualFileBytes";
    internal const string VersionedCatalogReference = "VersionedCatalogReference";
}
