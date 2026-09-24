# classicpages.csv file details

## Summary

This csv file contains one row for every classic page (wiki, web part, publishing, blog or ASPX page) that was discovered and assessed, including its page-modernization-readiness columns.

Confirmed Page Layout assets are physical inventory in [discovery.csv](csv-discovery.md), not content pages in this report. Publishing content pages retain their existing classification, friendly `Layout` name and Web Part analysis. A layout reference does not cause the layout body to be loaded or combined with the page body.

## Columns

The following columns are included:

Column|Description
------|-----------
PageUrl | Server-relative URL of the page
PageName | Display name of the page
PageType | The detected classic page type: `WikiPage`, `WebPartPage`, `PublishingPage`, `BlogPage`, `ASPXPage` or `DelveBlogPage`
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
LayoutUrl | Known server-relative URL from the Publishing page's `PublishingPageLayout` URL field, normalized as an escaped path (for example spaces become `%20`). Empty when no usable URL was acquired; never derived from `Layout`
LayoutReferenceStatus | `Resolved` when existing inventory confirms the layout asset; `Unresolved` for missing/unusable reference metadata or an unconfirmed target. `Unknown` is the unevaluated default for historical rows and non-Publishing pages
LayoutReferenceReason | Explicit reference evidence or unresolved reason, described below. Independent of page discovery/assessment status
HomePage | True when this page is the web's home (welcome) page, False when it is not, or empty when the web's welcome page could not be resolved
UncustomizedHomePage | True when this home page is still the default, uncustomized home page (only meaningful when `HomePage` is true)
ModifiedBy | Display name of the user who last modified the page (empty when `--skipuserinformation` was specified)
WebPartCount | Number of web parts found on the page
MappingPercentage | Percentage (0-100) of the page's web parts that have a known modern mapping. A page with no web parts is reported as 100%
UnmappedWebParts | Comma-separated list of the (short) web part type names on the page that do not have a known modern mapping
RemediationCode | The remediation code for this page type (`CP1`-`CP5`)
ScanId | Id of the assessment
SiteUrl | Fully qualified site collection URL
WebUrl | Relative URL of this web

## Relating Publishing pages to layout assets

Match `LayoutUrl` to a `RowType=Page` row's `Url` in [discovery.csv](csv-discovery.md), within the same `ScanId` and `SiteUrl` (case-insensitive, ignoring a trailing site URL slash). Available `SiteCollectionId` values must not conflict. Do **not** require equal `WebUrl`, `WebId`, `ListId` or `FileUniqueId`: a subweb page can use its site collection's catalog layout. The discovery row retains the asset's own physical identities and acquisition evidence.

Compare paths case-insensitively after the same normalization: same-origin absolute URLs become server-relative, dot segments are normalized, and path segments are decoded once and percent-encoded canonically. Spaces and `%20` therefore match; `%2520` remains distinct from `%20`. Web-relative names, foreign origins, malformed escapes, encoded separators, backslashes, repeated slashes and URLs containing query strings or fragments are unusable rather than guessed. `Layout` is a friendly description, not a URL or a join key. No catalog-path heuristic supplies missing asset-purpose evidence.

Resolution runs at scan finalization after all web workers have stored their discovery inventory, not while a particular web is being processed. A known URL is retained even when unresolved. There are no new target-discovery requests. Interrupted scans can retain `Unresolved` / `InventoryPending`; absence from the acquired inventory is not proof that a file does not exist.

Reason | Meaning
------ | -------
`ConfirmedLayoutAsset` | A unique same-scan, same-site physical row has the existing `PageLayout` / `Confirmed` asset-purpose decision and no retained Denied/Failed/Unknown discovery or assessment status
`InventoryPending` | Reference acquired; scan-wide relationship evaluation has not yet run
`ReferenceMetadataMissing` | The page URL field was absent or null
`ReferenceMetadataUnusable` | The field was not a supported native URL value or its URL could not be normalized safely; the description is never used to invent a URL
`ReferenceMetadataDenied`, `ReferenceMetadataFailed`, `ReferenceMetadataUnknown` | Acquisition was unavailable; later reference reads cannot erase this evidence. The existing discovery error columns retain acquisition exception details
`ReferenceMetadataConflict` | Repeated acquisition supplied different layout URLs; the earlier known URL and uncertainty are retained
`TargetNotDiscovered` | No matching physical inventory row in this scan/site; the reference URL remains known
`TargetAmbiguous` | Multiple eligible physical inventory rows share the locator; no target is guessed
`TargetPurposeUnavailable` | The target lacks confirmed asset-purpose metadata; inspect its `AssetPurposeReason` in discovery.csv
`TargetNotLayoutAsset` | The target has a confirmed purpose other than Page Layout
`TargetDiscoveryDenied`, `TargetDiscoveryFailed`, `TargetDiscoveryUnknown`, `TargetDiscoveryUnavailable` | Target discovery does not establish a successful relationship
`TargetAssessmentDenied`, `TargetAssessmentFailed`, `TargetAssessmentUnknown` | Target assessment retains unavailable evidence, even if its purpose was confirmed
`NotEvaluated` | Safe migration/model default; no reference evaluation has been recorded

Acquisition reasons can be semicolon-separated when more than one observation is retained. Reference resolution never rewrites target acquisition states/reasons, content-page classification, `Layout`, Web Part counts or analysis results. The additive database migration defaults `LayoutUrl` to null and reference status/reason to `Unknown` / `NotEvaluated`. Previously stored or resumed historical results are not repaired or backfilled.
