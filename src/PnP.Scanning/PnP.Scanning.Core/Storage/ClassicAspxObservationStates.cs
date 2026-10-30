namespace PnP.Scanning.Core.Storage;

/// <summary>Persisted coverage labels for physical ASPX observations, not proof of tenant-wide completeness.</summary>
internal static class ClassicAspxCoverageStates
{
    internal const string Unknown = "Unknown";
    internal const string Observed = "Observed";
    internal const string Incomplete = "Incomplete";
    internal const string Complete = "Complete";
    internal const string ChangedDuringScan = "ChangedDuringScan";
}

/// <summary>Persisted metadata read outcomes for physical ASPX file observations.</summary>
internal static class ClassicAspxReadStates
{
    internal const string NotAttempted = "NotAttempted";
    internal const string NotRequested = "NotRequested";
    internal const string NotApplicable = "NotApplicable";
    internal const string Unavailable = "Unavailable";
    internal const string Available = "Available";
}
