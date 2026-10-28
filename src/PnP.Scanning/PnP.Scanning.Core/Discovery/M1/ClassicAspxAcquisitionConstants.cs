namespace PnP.Scanning.Core.Discovery.M1;

/// <summary>Stable acquisition scope names persisted in request receipts and coverage evidence.</summary>
internal static class ClassicAspxAcquisitionScopes
{
    internal const string Physical = "Physical";
    internal const string HomePageOnly = "HomePageOnly";
    internal const string HomePageItems = "Items:HomePageOnly";
    internal const string Lists = "Lists";
    internal const string ContentTypesPrefix = "ContentTypes:";
    internal const string ItemsPrefix = "Items:";
}

/// <summary>Stable reasons for incomplete or rejected ASPX acquisition; these values are report contracts.</summary>
internal static class ClassicAspxAcquisitionErrorCodes
{
    internal const string PhysicalIdentityUnproved = "physical_identity_or_scope_unproved";
    internal const string HomePageBackingUnconfirmed = "homepage_backing_requires_targeted_item_confirmation";
    internal const string HomePageItemsExcluded = "explicit_homepage_scope_does_not_authorize_all_items";
    internal const string MissingListGuid = "list_guid_missing";
    internal const string ContentTypeLimit = "content_type_metadata_limit";
    internal const string AmbiguousContentType = "content_type_identity_ambiguous";
    internal const string MissingPositiveItemId = "item_positive_id_missing";
    internal const string ItemRendererUnproved = "item_content_type_renderer_unproved";
    internal const string ListRendererUnloaded = "list_default_renderer_unloaded";
    internal const string IncompleteContentTypes = "content_type_scope_incomplete_no_renderer_fallback";
    internal const string TransportFailure = "transport_failure";
    internal const string ResponseUriDrift = "pagination_response_uri_drift";
    internal const string UnsafeOrRepeatedCursor = "pagination_unsafe_or_repeated_cursor";
    internal const string IncompleteCollection = "m1_collection_incomplete";
    internal const string UnsupportedRendererPath = "renderer_path_unsupported";
    internal const string UnresolvedCatalogMember = "platform_catalog_member_unresolved";
    internal const string AmbiguousCatalogAlias = "platform_catalog_alias_ambiguous";
    internal const string FileResolutionPrefix = "file_resolution_";
    internal const string UnprovedFileIdentity = "file_identity_or_alias_unproved";
    internal const string FailedFileSourceRead = "file_source_read_failed";
    internal const string UnprovedFileSource = "file_source_unproved";
    internal const string OutsideSiteCollection = "reference_outside_site_collection";
    internal const string RootClientUnavailable = "same_site_root_client_unavailable";
    internal const string RootClientFailurePrefix = "same_site_root_client_";
    internal const string RootClientScopeMismatch = "same_site_root_client_scope_mismatch";
}

/// <summary>Distinct provenance tags for resource discovery and actual Item-to-resource observations.</summary>
internal static class ClassicAspxAcquisitionSources
{
    internal const string PhysicalSource = "PhysicalSourceObservation";
    internal const string PhysicalFile = "PhysicalFileObservation";
    internal const string ItemFile = "Item.File";
    internal const string ItemFileExpansion = "Item.File expansion";
    internal const string RestItemFileExpansion = "REST:Item.File expansion";
    internal const string ResolvedItemFile = "ResolvedItemFileBacking";
    internal const string ExactBackingItem = "ResolvedFile exact ListId/ItemId";
    internal const string ContentTypeDisplayForm = "ContentType.DisplayFormUrl";
    internal const string MostSpecificContentTypeDisplayForm = "MostSpecificListContentType.DisplayFormUrl";
    internal const string ListDisplayForm = "List.DefaultDisplayFormUrl";
    internal const string PublishingLayout = "PublishingPageLayout";
    internal const string ItemPublishingLayout = "Item.PublishingPageLayout";
}

/// <summary>Names for acquisition evidence stages, record categories and stable record-key prefixes.</summary>
internal static class ClassicAspxAcquisitionEvidenceNames
{
    internal const string Page = "Page";
    internal const string Scope = "Scope";
    internal const string Surface = "Surface";
    internal const string Pagination = "Pagination";
    internal const string RequestPage = "RequestPage";
    internal const string Gap = "Gap";
    internal const string Acquisition = "M1Acquisition";
    internal const string FileResolution = "FileResolution";
    internal const string FileSource = "FileSource";
    internal const string FileClientRoute = "FileClientRoute";
    internal const string ReadFileContentPrefix = "ReadFileContentAsync:";
    internal const string NativeMethod = "NativeM1Acquisition";
    internal const string RecordPrefix = "m1:";
    internal const string WebScopePrefix = "m1:web:";
    internal const string TransportErrorSchema = "transport-error";
    internal const string GetMethod = "GET";
}
