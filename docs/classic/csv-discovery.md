# discovery.csv file details

## Summary

This csv file contains the physical ASPX inventory and acquisition evidence collected by the classic pages assessment. The existing assessment ScanId, database and report lifecycle own every row.

`Page` rows represent physical ASPX files. `Scope` rows represent tenant, site, web, list, folder and API surfaces. `Reference` rows record Forms, Views and welcome-page references. `Pagination` rows record sanitized request-chain evidence. `Gap` rows retain acquisition gaps that are not owned by one scope. After post-scan processing, the `Summary` row records the scan-wide coverage verdict.

> [!NOTE]
> A finished assessment can contain `Denied`, `Failed`, `Partial` or `Unknown` coverage rows. These rows identify parts of the selected tenant or sites with incomplete inspection. Discovered pages remain as Page rows when their assessment fails. `discovery.csv` records discovery and assessment gaps through Scope and Gap rows together with the `ErrorStage`, `ErrorCodes` and `ErrorDetail` columns.

## Columns

The following columns are included:

Column|Description
------|-----------
RecordKey | Stable key for the page or discovery scope within the assessment.
RowType | `Page`, `Scope`, `Reference`, `Pagination`, `Gap` or `Summary`.
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
DiscoveryStatus | Page existence, scope completion, reference disposition, pagination completion, gap state or the final scan-wide coverage verdict, depending on `RowType`. Summary verdicts are `CompleteTenantVerified`, `CompleteAuthorizedSurface`, `CompleteDeclaredSubset`, `Incomplete` or `Unknown`.
AssessmentStatus | Page enrichment result: `Complete`, `NotSelected`, `NotApplicable` or `Failed`. Empty for Scope rows.
ExpectedChildCount | Number of child scopes or objects expected when the source can provide a reliable count.
ObservedChildCount | Number of child scopes or objects that were observed.
ErrorStage | Discovery or assessment stage that produced an error or coverage gap.
ErrorCodes | One or more error or coverage-gap codes.
ErrorDetail | Details associated with `ErrorCodes`.
EvidenceJson | Structured surface, reference, pagination or summary evidence. Continuation values are represented by hashes rather than raw tokens.
ObservedAtUtc | UTC timestamp at which the page or scope was recorded.
ScanId | Id of the assessment.
SiteUrl | Fully qualified site collection URL associated with the row.
WebUrl | Relative URL of the web associated with the row.
