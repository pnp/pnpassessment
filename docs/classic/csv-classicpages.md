# classicpages.csv file details

## Summary

For a native pipeline assessment this CSV comes from the digest-verified immutable analysis publication, not just the legacy URL-keyed table. It contains the following union:

- Physical ASPX files that meet all three conditions:
  - **NoExecute not equal to true** (both null and false are included).
  - An exact source handler in `Microsoft.SharePoint.WebPartPages.WikiEditPage`, `Microsoft.SharePoint.WebPartPages.WebPartPage`, `Microsoft.SharePoint.Publishing.TemplateRedirectionPage`, or `System.Web.UI.Page`. A source with no `Inherits` declaration resolves to `System.Web.UI.Page`.
  - **Outside the List product family.** A file registered as a list view or list form (`IsListView` or `IsListForm` is true) belongs to the List product family and is reported in [aspx.csv](csv-aspx.md) only. Files with unknown (empty) list-role evidence are included. A content page that hosts a list view web part keeps `IsListView` false and is included; see [List view pages and pages that host a list view](csv-aspx.md#list-view-pages-and-pages-that-host-a-list-view).
- Actual independent list items from the observed Posts template **301**. They are not subject to the ASPX handler/NoExecute gate.

The List product family is the only product-family condition. Experience, official state, catalog role and backing-item availability are reported as columns and leave the selection unchanged. Unknown handlers are not guessed from metadata or short names. [aspx.csv](csv-aspx.md) independently retains **all** physical ASPX facts, including those excluded here.

File identity is `(SiteCollectionId, FileUniqueId)`; Posts identity is `(SiteCollectionId, ListId, ListItemId)`, inside the assessment. A shared display-form renderer is not item identity and does not donate its usage to every post. Equal hashes do not merge different native files. Distinct native objects that share one historical report URL key are never merged: the object with the lowest `ContentKey` keeps the row and each other object is listed in `classicpagesomitted.csv` (see [Pages left out of the report](#pages-left-out-of-the-report)).

`classicpagessummary.json` records the same written Page count, file/Posts counts, the physical ASPX denominator, true/false/unknown NoExecute counts for the file branch, and `OmittedPageCount`, the number of rows in `classicpagesomitted.csv`. Exported page-dependent Web/site/publishing summaries, web-part inventories and usage rows are recomputed from this same selected set, without changing SQL assessment tables. Retained non-page component counts are unchanged.

These related CSVs are selected-Page views, not deletion of captured evidence. The complete original parts, native file facts, component rows, immutable source/analysis publication and existing SQL tables remain intact. Discovery coverage remains a separate observed-acquisition denominator rather than being rewritten to claim that filtered-out files were absent.

Historical assessments with no native pipeline keep their existing legacy CSV contract. Their summary explicitly says `LegacyContract;NativeSourceUnavailable`; it does not claim that missing original source was acquired or that the native predicate was proved. An incomplete newer native analysis cannot fall back to older report rows.

A page whose list item could not be read — a web's root `default.aspx` and other files that have no backing list item at all — is still assessed from its file alone: its web parts are read through the file's web part manager. The columns that come from the list item (`ListUrl`, `ListTitle`, `ListId`, `ListItemId`, `ModifiedAt`, `ModifiedBy`) are then empty rather than filled with a stand-in, and [discovery.csv](csv-discovery.md) records why in `ErrorCodes`.

## Pages left out of the report

Every post has an address. A Posts list that declares a display form uses it, for example `/sites/a/Lists/Posts/Post.aspx?ID=7`. A Posts list with no usable display form is addressed through SharePoint's list form redirect, `<web>/_layouts/15/listform.aspx?PageType=4&ListId=<list id>&ID=<item id>`, built from the observed list id and item id, so the post keeps its row. Usage counts for such a post are filled when the audit log holds that exact address and stay empty (`ItemLocatorNotObserved`) otherwise.

The report identifies a page by its site, web and address. A selected page that this key cannot carry is left out of `classicpages.csv` and of the summaries built from it, and is written to `classicpagesomitted.csv` with its reason. The assessment and the export complete, and the `report` command prints how many pages were left out.

Reason | Meaning
-------|--------
NoAddress | The page has no address the report can show.
SharedAddress | Another page in the same web has the same address. The page with the lowest `ContentKey` keeps the row, and `SharesAddressWith` holds that page's `ContentKey`.
NotAnalyzed | The file meets the selection above and the assessment holds no analyzed page for it.

`classicpagesomitted.csv` has the columns `ScanId`, `SiteUrl`, `WebUrl`, `PageUrl`, `PageName`, `PageType`, `ObjectKind`, `ContentKey`, `ListId`, `ListItemId`, `FileUniqueId`, `Reason` and `SharesAddressWith`. It is written for every native pipeline assessment and holds only its header when every selected page is in `classicpages.csv`. The same rule fills the assessment's report tables at the end of a run, where the log records the count for each reason. The native publication and [aspx.csv](csv-aspx.md) keep every one of these objects.

## Columns

The following columns are included:

Column|Description
------|-----------
PageUrl | Server-relative URL of the page
PageName | Display name of the page
PageType | Canonical type from the admitted exact handler: `WikiPage`, `WebPartPage`, `PublishingPage` or `ASPXPage`; actual independent Posts are `BlogPage`. The native pipeline does not admit pointpub as Pages.
AssessmentPageType | Original readiness-assessment type, retained independently of the exact source-handler export label.
ListUrl | Server-relative URL of the library the page lives in
ListTitle | Title of the library the page lives in
ListId | Id of the library the page lives in
SiteCollectionId | Id of the site collection that owns the page
WebId | Id of the web that owns the page
FileUniqueId | Unique id of the physical page file
ListItemId | List item id of the page in its owning library
DiscoveryStatus | Physical discovery result for the page, normally `Discovered`
AssessmentStatus | Page assessment result, such as `Complete` or `Failed`
ModifiedAt | When the page was last modified
Layout | The detected page layout (e.g. a wiki `TwoColumns`, a web part page `FullPageVertical`, or the publishing page layout name such as `ArticleLeft`)
CustomizedPageStatus | SharePoint customization status of the page file: `Uncustomized` (ghosted), `Customized` (unghosted), or `None` (no customization classification). Empty when unavailable; never infer ghosting from an empty value or `None`.
LayoutUrl | URL of the publishing page's referenced layout file, as stored in `PublishingPageLayout`. Empty for non-publishing pages or when the reference is unavailable.
LayoutCustomizedPageStatus | SharePoint customization status of the referenced publishing layout file: `Uncustomized`, `Customized`, or `None`. Populated during post-scanning from referenced discovery rows; missing statuses (including `--homepageonly`) use a cached fallback with SDK retries. Conflicting observations retain the latest known status and record `changed_during_scan` in `discovery.csv`. Empty for non-publishing pages or when unavailable. Layout failures are recorded as `PageLayoutMetadata` without changing the page's `AssessmentStatus`.
HomePage | True when this page is the web's home (welcome) page, False when it is not, or empty when the web's welcome page could not be resolved
UncustomizedHomePage | Nullable authoritative primary API result, or the canonical true/false result when both sealed Web/file fallback receipts establish a completed fallback. Failed/unacquired fallback is empty, not false.
ModifiedBy | Email address of the user who last modified the page, or the display name when the account has no email address (empty when `--skipuserinformation` was specified)
WebPartCount | Actual part count when sealed acquisition and analysis provenance establish a successful read; a genuine empty read is 0. Unacquired/inapplicable values, including Posts, are empty.
MappingPercentage | Measured readiness percentage only when actually calculated. File-only pages with a successful empty part read retain WebPartCount=0 but have an empty percentage and MappingReadState=WithheldFileOnlyEmpty. Real computed 0% and 100% remain numeric.
UnmappedWebParts | Comma-separated list of the (short) web part type names on the page that do not have a known modern mapping
RemediationCode | The remediation code for this page type (`CP1`-`CP5`)
ScanId | Id of the assessment
SiteUrl | Fully qualified site collection URL
WebUrl | Relative URL of this web
ObjectKind / ContentKey | Discriminated native file/item identity, not a URL/hash key.
SourceKind | `File` for a page that is a physical .aspx file, `ListItem` for a blog post, which is a list item without a file of its own.
ListTemplate | Template number of the list or library the page belongs to, such as `119` for a wiki page library, `101` for a document library, `850` for a publishing Pages library and `301` for a blog Posts list. Empty when the page belongs to no list.
ContentReadState | `Available` when the source of the page file was read, `NotApplicable` for a blog post, and another read state when the source was not read.
ProductFamily | Where the file belongs: `User` for a page that people author, `catalog_…` (for example `catalog_masterpage`) for a file in a gallery, and `Unknown` when the role of the file is not established, and for a blog post.
Experience | `Classic` for a page file, `Unknown` when the role of the file is not established, and for a blog post.
ImpactStatus / ImpactedBy | `Included` for a classic page that people author; `ImpactedBy` then links to the message center post that covers such pages. `Excluded` for a file in a gallery, `Unknown` when the classification is not established, and for a blog post; `ImpactedBy` is empty for both.
ClassificationReason | The rule that gave the classification above, such as `ClassicUserPage;MC1464926`, `CatalogAsset;ExactClassicHandler` or `RoleEvidenceIncomplete`. A blog post names how it was observed, for example `ObservedListItemAndRendererDeclaration`.
AnalysisRunId / SnapshotId | Exact immutable publication and source selected for this export.
PageHandler / SourceSha256 / SourceReadState / HandlerResolution | Exact captured source evidence. Empty for independent items or unavailable source.
NoExecute / NoExecuteRaw / NoExecuteReadState | Nullable trusted Boolean plus the original server text and read outcome. True excludes only the file branch; unknown remains unknown and is included when the handler qualifies.
IsCatalog / IsListView / IsListForm / RoleReadState | Captured role evidence. Every file row here has `IsListView` and `IsListForm` either false or empty, because a true value places the file in the List product family. `IsCatalog` and `RoleReadState` are informational.
Official / OfficialReadState | Unknown/NotAcquired when no independent authoritative official-file observation exists. No inference from path or role.
AuditViewsCount / AuditCreatesCount / AuditEditsCount / AuditUniqueUsers | Exact content-locator usage including the Posts item query. Unknown/skipped/failed counts stay empty; partial counts are labelled partial.
UsageQueryStatus / AuditWindowStart / AuditWindowEnd | Fixed audit-window coverage. A successful fully covered query with no matching events can report zero; an unavailable query cannot.
UsageAttributionState | ExactContentLocator when events carry the actual content locator; Posts with no item-specific record stay ItemLocatorNotObserved with empty counts even if query coverage succeeded. Renderer activity is never assigned to Posts.
WebPartsReadState / MappingReadState | Sealed part acquisition and explicit computed/withheld/unavailable readiness state, independently of the broad page AssessmentStatus.
CanModernizeReadState / WebHomeFallbackReadState / HomeFallbackReadState | Primary API, Web fallback and file fallback acquisition states used to establish nullable home-page readiness.

Audit statistics are reduced with `classic-pages-v5` rules while the acquisition shape remains `classic-objects-v4`. This fixes query-addressed Posts events without changing sealed source bytes. Previously published v4 statistics are not silently reinterpreted or rewritten; a new explicit analysis run is required to produce corrected results.
