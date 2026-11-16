# discovery.csv file details

## Summary

This csv file contains the physical ASPX inventory and acquisition evidence collected within the requested page scope. The existing assessment ScanId, database and report lifecycle own every row. A normal classic pages assessment uses the full physical inventory scope. With `--homepageonly`, discovery resolves only each web's configured welcome page so the scan remains a fast home-page assessment.

`Url` identifies each physical page, while `FileName` provides its leaf name directly. Final page classification belongs to [classicpages.csv](csv-classicpages.md); discovery rows retain the physical identity, acquisition coverage and assessment disposition needed to explain which files did or did not produce classic-page results.

`Page` rows represent physical ASPX files. `Scope` rows represent tenant, site, web, list, folder and API surfaces. `Reference` rows record Forms, Views and welcome-page references. `Pagination` rows record sanitized request-chain evidence. `Gap` rows retain acquisition gaps that are not owned by one scope. After post-scan processing, the `Summary` row records the scan-wide coverage verdict.

> [!NOTE]
> A finished assessment can contain `Denied`, `Failed`, `Partial` or `Unknown` coverage rows. These rows identify parts of the requested scope with incomplete inspection. Discovered pages remain as Page rows when their assessment fails. `discovery.csv` records discovery and assessment gaps through Scope and Gap rows together with the `ErrorStage`, `ErrorCodes` and `ErrorDetail` columns. The Summary evidence records `pageScope=HomePageOnly` for a scoped home-page scan and `pageScope=FullInventory` otherwise.

## Summary coverage verdicts

The `Summary` row stores the scan-wide coverage verdict in `DiscoveryStatus`. Verdict evaluation follows the order shown below, so uncertainty and incomplete coverage take precedence over successful scope classifications.

Verdict | Condition
--------|----------
`Unknown` | No Scope rows were recorded, `assessment:site-selection` is absent or unrecognized, a coverage row remains `Pending` or `Unknown`, or an acquisition gap prevents the scanner from establishing the observed denominator or identity.
`Incomplete` | A coverage row is `Denied`, `Failed`, `Partial` or `Cancelled`, or an expected child scope was not observed.
`CompleteDeclaredSubset` | All recorded coverage succeeded and the assessment used an explicit site selection or `HomePageOnly` page selection.
`CompleteTenantVerified` | All recorded coverage succeeded and `assessment:site-selection` records a completed `Tenant` enumeration.

## Listing under throttling

Collection lists the files and folders of each web with its own requests. When SharePoint throttles one of them (HTTP 429 or 503), the request waits for the time SharePoint directs, up to 10 minutes per wait, and is then sent again, for up to five attempts. Every other listing request to the same SharePoint host holds for the same time. The hold also applies when SharePoint throttles another request of the assessment, such as a file download. The log records each wait in a line that starts with `[Throttling]`.

A listing request that is still throttled at its last attempt, or that SharePoint directs to wait longer than 10 minutes, is given up. Its `Pagination` row has `DiscoveryStatus` `Failed` and one of the codes below, and the scope it belongs to is `Failed`, so the inventory holds only the files that other requests returned for that location.

Code | Meaning
-----|--------
`json_throttle_attempts_exhausted` | SharePoint throttled every attempt.
`json_throttle_delay_exceeds_budget` | SharePoint directed a wait longer than the request honors, or a wait the request cannot read.

Each request given up this way counts as an incomplete read in the Issues column of `status` and `list`. The `report` command prints how many locations stayed incompletely listed, by the rule of `incompleteScopeCount` in the Summary evidence, and how many listing requests throttling stopped. A new assessment lists those locations again.

## Columns

The following columns are included:

Column|Description
------|-----------
RecordKey | Stable key for the page or discovery scope within the assessment.
RowType | `Page`, `Scope`, `Reference`, `Pagination`, `Gap` or `Summary`.
ScopeType | Type of scope or object represented by the row, such as `Tenant`, `SiteCollection`, `PageSelection`, `Web`, `List`, `Folder`, `Surface` or `File`.
ParentScopeKey | Record key of the parent discovery scope.
Url | Server-relative page URL for a Page row. For a Scope row this is the inspected scope or endpoint.
SiteCollectionId | Id of the owning site collection when it could be resolved.
WebId | Id of the owning web when it could be resolved.
ListId | Id of the owning list or library when the row is list-backed.
FolderUniqueId | Unique id of the owning or inspected folder when available.
FileUniqueId | Unique id of the discovered file for Page rows when available.
ListItemId | List item id of the discovered file when the page is list-backed.
FileName | File name of the discovered page.
CustomizedPageStatus | SharePoint page-file customization status: `Uncustomized` (ghosted), `Customized` (unghosted), or `None` (no customization classification). Empty when unavailable or on non-Page rows. Unrecognized values produce `PageCustomizationMetadata` errors rather than an inferred status. Repeated observations retain known values when a later read omits the status.
SetupPath | The file's `vti_setuppath`: the on-disk template a ghosted file was provisioned from. Empty means the file is unghosted, so it is user authored or user uploaded.
HomePage | True or False when the web's welcome page could be resolved and compared with this page. Empty means the home-page state is unknown.
LibraryHidden | True when the owning library is hidden, False when it is visible, or empty when this could not be determined.
ObservationMethod | API surface or adapter that observed the page or scope.
DiscoveryStatus | Page existence, scope completion, reference disposition, pagination completion, gap state or the final scan-wide coverage verdict, depending on `RowType`. The Summary values and their conditions are defined in Summary coverage verdicts.
AssessmentStatus | Page enrichment result: `Complete`, `NotSelected`, `NotApplicable` or `Failed`. Empty for Scope rows. A home-page-only run discovers only the selected welcome pages rather than emitting every other page as `NotSelected`. A file whose list item could not be read is still assessed from the file alone, with the inapplicable item-dependent operations recorded in `ErrorCodes`: `list_association_absent` when the file belongs to no list, which is the normal state of a web-root file; `list_item_absent` for a confirmed absence of any backing item; `owning_list_unresolved` when the file names an owning list that was not matched in the web; `list_association_unknown` when the file lies inside a loaded list, so it is list-backed, but no observation of it reported a list association — the identity is unknown rather than absent; and `system_page_template` when `SetupPath` proves the file ghosts from the stock list view or list form template and therefore carries no page content of its own. The first two are confirmed structural absences and do not make the page a failure. The next two are read gaps and leave the assessment `Failed`, because the item metadata may exist and was simply unreachable. `registered_list_view_or_form` marks a file without a backing item that is a registered list view or list form: its web parts are left unread and it is `NotApplicable`.
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
