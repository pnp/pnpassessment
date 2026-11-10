# classicpages.csv file details

## Summary

For a native pipeline assessment this CSV comes from the digest-verified immutable analysis publication, not just the legacy URL-keyed table. It contains the following union:

- Physical ASPX files with **NoExecute not equal to true** (both null and false are included) and an exact source handler in `Microsoft.SharePoint.WebPartPages.WikiEditPage`, `Microsoft.SharePoint.WebPartPages.WebPartPage`, `Microsoft.SharePoint.Publishing.TemplateRedirectionPage`, or `System.Web.UI.Page`.
- Actual independent list items from the observed Posts template **301**. They are not subject to the ASPX handler/NoExecute gate.

No product-family, experience, official, catalog, registered-list-role or backing-item requirement is added to that predicate. Unknown handlers are not guessed from metadata or short names. [aspx.csv](csv-aspx.md) independently retains **all** physical ASPX facts, including those excluded here.

File identity is `(SiteCollectionId, FileUniqueId)`; Posts identity is `(SiteCollectionId, ListId, ListItemId)`, inside the assessment. A shared display-form renderer is not item identity and does not donate its usage to every post. Equal hashes do not merge different native files. Distinct native objects colliding on the historical report URL key fail explicitly; no URL `Distinct` silently drops them.

`classicpagessummary.json` records the same written Page count, file/Posts counts, the physical ASPX denominator, and true/false/unknown NoExecute counts for the file branch. Exported page-dependent Web/site/publishing summaries, web-part inventories and usage rows are recomputed from this same selected set, without changing SQL assessment tables. Retained non-page component counts are unchanged.

These related CSVs are selected-Page views, not deletion of captured evidence. The complete original parts, native file facts, component rows, immutable source/analysis publication and existing SQL tables remain intact. Discovery coverage remains a separate observed-acquisition denominator rather than being rewritten to claim that filtered-out files were absent.

Historical assessments with no native pipeline keep their existing legacy CSV contract. Their summary explicitly says `LegacyContract;NativeSourceUnavailable`; it does not claim that missing original source was acquired or that the native predicate was proved. An incomplete newer native analysis cannot fall back to older report rows.

A page whose list item could not be read — a web's root `default.aspx` and other files that have no backing list item at all — is still assessed from its file alone: its web parts are read through the file's web part manager. The columns that come from the list item (`ListUrl`, `ListTitle`, `ListId`, `ListItemId`, `ModifiedAt`, `ModifiedBy`) are then empty rather than filled with a stand-in, and [discovery.csv](csv-discovery.md) records why in `ErrorCodes`.

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
UncustomizedHomePage | True when this home page is still the default, uncustomized home page (only meaningful when `HomePage` is true)
ModifiedBy | Display name of the user who last modified the page (empty when `--skipuserinformation` was specified)
WebPartCount | Measured part count when readiness assessment completed for a Wiki/WebPart/Publishing file; empty for unacquired or inapplicable measurements, including Posts.
MappingPercentage | Measured readiness percentage when available; empty is unknown/inapplicable, not a successful 0 or 100. Read with AssessmentStatus and AssessmentPageType.
UnmappedWebParts | Comma-separated list of the (short) web part type names on the page that do not have a known modern mapping
RemediationCode | The remediation code for this page type (`CP1`-`CP5`)
ScanId | Id of the assessment
SiteUrl | Fully qualified site collection URL
WebUrl | Relative URL of this web
ObjectKind / ContentKey | Discriminated native file/item identity, not a URL/hash key.
AnalysisRunId / SnapshotId | Exact immutable publication and source selected for this export.
PageHandler / SourceSha256 / SourceReadState / HandlerResolution | Exact captured source evidence. Empty for independent items or unavailable source.
NoExecute / NoExecuteRaw / NoExecuteReadState | Nullable trusted Boolean plus the original server text and read outcome. True excludes only the file branch; unknown remains unknown and is included when the handler qualifies.
IsCatalog / IsListView / IsListForm / RoleReadState | Captured role evidence, not additional Page-admission filters.
Official / OfficialReadState | Unknown/NotAcquired when no independent authoritative official-file observation exists. No inference from path or role.
AuditViewsCount / AuditCreatesCount / AuditEditsCount / AuditUniqueUsers | Exact content-locator usage including the Posts item query. Unknown/skipped/failed counts stay empty; partial counts are labelled partial.
UsageQueryStatus / AuditWindowStart / AuditWindowEnd | Fixed audit-window coverage. A successful fully covered query with no matching events can report zero; an unavailable query cannot.
