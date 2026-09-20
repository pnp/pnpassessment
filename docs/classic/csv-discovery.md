# discovery.csv file details

## Summary

This csv file contains the physical ASPX inventory collected by the classic pages assessment. It contains both discovered pages and the scopes that were inspected while looking for pages.

Rows with `RowType=Page` represent physical ASPX files. Rows with `RowType=Scope` represent a tenant, site, web, list, folder or API surface that was inspected. Scope rows make incomplete discovery visible without creating page rows for files that were not observed.

> [!NOTE]
> A finished assessment can contain `Denied`, `Failed`, `Partial` or `Unknown` scope rows. Use these rows to identify parts of the selected tenant or sites that could not be fully inspected. Page assessment failures do not remove the corresponding discovered page from this file.
> Discovery and assessment gaps are reported through the Scope rows and the `ErrorStage`, `ErrorCodes` and `ErrorDetail` columns. The Classic report does not generate a separate discovery gaps csv file.

## Columns

The following columns are included:

Column|Description
------|-----------
RecordKey | Stable key for the page or discovery scope within the assessment.
RowType | `Page` for a discovered physical ASPX file or `Scope` for an inspected discovery scope.
ScopeType | Type of scope or object represented by the row, such as `Tenant`, `SiteCollection`, `Web`, `List`, `Folder`, `Surface` or `File`.
ParentScopeKey | Record key of the parent discovery scope.
Url | Server-relative page URL for a Page row. For a Scope row this is the inspected scope or endpoint.
SiteCollectionId | Id of the owning site collection when it could be resolved.
WebId | Id of the owning web when it could be resolved.
ListId | Id of the owning list or library when the row is list-backed.
FolderUniqueId | Unique id of the owning or inspected folder when available.
FileUniqueId | Unique id of the discovered file for Page rows when available.
ListItemId | List item id of the discovered file when the page is list-backed.
FileName | File name of the discovered page.
PageType | Detected page type when page metadata could be loaded.
ContentTypeId | Content type id of the page's list item when available.
HomePage | True or False when the web's welcome page could be resolved and compared with this page. Empty means the home-page state is unknown.
LibraryHidden | True when the owning library is hidden, False when it is visible, or empty when this could not be determined.
ObservationMethod | API surface or adapter that observed the page or scope.
DiscoveryStatus | `Discovered` for Page rows. Scope rows can report `Pending`, `Complete`, `Empty`, `PolicyExcluded`, `Denied`, `Failed`, `Partial`, `Cancelled` or `Unknown`.
AssessmentStatus | Page enrichment result: `Complete`, `NotSelected`, `NotApplicable` or `Failed`. Empty for Scope rows.
ExpectedChildCount | Number of child scopes or objects expected when the source can provide a reliable count.
ObservedChildCount | Number of child scopes or objects that were observed.
ErrorStage | Discovery or assessment stage that produced an error or coverage gap.
ErrorCodes | One or more error or coverage-gap codes.
ErrorDetail | Details associated with `ErrorCodes`.
ObservedAtUtc | UTC timestamp at which the page or scope was recorded.
ScanId | Id of the assessment.
SiteUrl | Fully qualified site collection URL associated with the row.
WebUrl | Relative URL of the web associated with the row.
