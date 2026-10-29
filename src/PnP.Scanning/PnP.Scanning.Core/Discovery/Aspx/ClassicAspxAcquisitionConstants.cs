namespace PnP.Scanning.Core.Discovery.Aspx;

/// <summary>Stable acquisition scope names persisted in request receipts and coverage evidence.</summary>
internal static class ClassicAspxAcquisitionScopes
{
    internal const string Physical = "Physical";
    internal const string ItemEnumerationPolicy = "ItemEnumerationPolicy";
    internal const string ListItemsNotEnumerated = "ListItemsNotEnumerated";
}

/// <summary>Stable reasons for incomplete or rejected ASPX acquisition; these values are report contracts.</summary>
internal static class ClassicAspxAcquisitionErrorCodes
{
    internal const string PhysicalIdentityUnproved = "physical_identity_or_scope_unproved";
    internal const string FailedFileSourceRead = "file_source_read_failed";
    internal const string UnprovedFileSource = "file_source_unproved";
    internal const string OutsideSiteCollection = "reference_outside_site_collection";
    internal const string RootClientUnavailable = "same_site_root_client_unavailable";
    internal const string RootClientFailurePrefix = "same_site_root_client_";
    internal const string RootClientScopeMismatch = "same_site_root_client_scope_mismatch";
}

/// <summary>Provenance tags for observed physical ASPX resources.</summary>
internal static class ClassicAspxAcquisitionSources
{
    internal const string PhysicalSource = "PhysicalSourceObservation";
    internal const string PhysicalFile = "PhysicalFileObservation";
}

/// <summary>Names for acquisition evidence stages, record categories and stable record-key prefixes.</summary>
internal static class ClassicAspxAcquisitionEvidenceNames
{
    internal const string Page = "Page";
    internal const string Scope = "Scope";
    internal const string Gap = "Gap";
    internal const string Acquisition = "AspxAcquisition";
    internal const string FileSource = "FileSource";
    internal const string FileClientRoute = "FileClientRoute";
    internal const string ReadFileContentPrefix = "ReadFileContentAsync:";
    internal const string NativeMethod = "NativeAspxAcquisition";
    internal const string RecordPrefix = "aspx:";
    internal const string WebScopePrefix = "aspx:web:";
}
