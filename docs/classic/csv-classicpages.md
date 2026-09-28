# classicpages.csv file details

## Summary

This csv file contains one row for every classic page (wiki, web part, publishing, blog or ASPX page) that was discovered and assessed, including its page-modernization-readiness columns.

For scans with recorded `PublishingLayoutRuleVersion = 1`, only confirmed source-declared members of the `Microsoft.SharePoint.Publishing.PublishingLayoutPage` CLR family are excluded as layout handlers. They remain physical inventory in [discovery.csv](csv-discovery.md), not content pages in this report. ContentType alone does not exclude a file; unresolved ancestry retains Unknown purpose while preserving existing eligible page assessment. The gate precedes metadata loading, enrichment and counters, rather than filtering this CSV or publishing summaries. Version `0` scans retain their original authority and historical stored results remain unchanged and exportable.

Publishing content-page envelopes, including `TemplateRedirectionPage`, retain their physical identity, existing classification, friendly `Layout` name, known `LayoutUrl` and Web Part analysis. Selection of or delegation to a separate layout handler (including a later `HttpContext.Current.Handler` change) does not replace the outer physical file's type evidence. A layout reference does not cause the layout body to be loaded or combined with the page body. Reference resolution below is separate from the admission predicate.

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
LayoutReferenceStatus | For rule version `1`, `Resolved` when existing inventory confirms the target's PublishingLayoutPage CLR-family membership; `Unresolved` for missing/unusable reference metadata or an unconfirmed/non-family target. `Unknown` is the unevaluated default for historical rows and non-Publishing pages
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

## Relating Publishing pages to layout handlers

Match `LayoutUrl` to a `RowType=Page` row's `Url` in [discovery.csv](csv-discovery.md), within the same `ScanId` and `SiteUrl` (case-insensitive, ignoring a trailing site URL slash). Available `SiteCollectionId` values must not conflict. Do **not** require equal `WebUrl`, `WebId`, `ListId` or `FileUniqueId`: a subweb page can use its site collection's catalog layout. The discovery row retains the asset's own physical identities and acquisition evidence.

Compare paths case-insensitively after the same normalization: same-origin absolute URLs become server-relative, dot segments are normalized, and path segments are decoded once and percent-encoded canonically. Spaces and `%20` therefore match; `%2520` remains distinct from `%20`. Web-relative names, foreign origins, malformed escapes, encoded separators, backslashes, repeated slashes and URLs containing query strings or fragments are unusable rather than guessed. `Layout` is a friendly description, not a URL or a join key.

For scans with recorded `Scans.PublishingLayoutRuleVersion = 1`, a unique target must satisfy the shared `PublishingLayoutTypeEvidence.IsConfirmedMember` predicate: `RowType=Page`, `PageTypeEvidenceOrigin=DeclaredSource`, `PageTypeSourceStatus=Available`, `PageTypeResolutionStatus=Resolved` and `PublishingLayoutFamily=Member`. These are source-declared type/ancestry results, **not** observed runtime handlers. Inspect `DeclaredPageType`, `ResolvedPageType`, `PageTypeReason` and `PageTypeEvidenceJson` in discovery.csv for the supported identity binding, ancestry provenance and acquisition evidence. ContentType, `AssetPurpose`, catalog location and layout selection cannot replace this proof. A confirmed root/direct/indirect family member can resolve outside the catalog and without Page Layout ContentType. A selected layout with proven non-family type remains an unresolved family relationship; selection alone does not make it a PublishingLayoutPage handler.

Resolution runs at scan finalization after all web workers have stored their discovery inventory, not while a particular web is being processed. A known URL is retained even when unresolved. There are no new target-discovery requests, reference-driven source fetches or layout-body analysis. Interrupted new scans can retain `Unresolved` / `InventoryPending`; their recorded authority is preserved through restart. Absence from the acquired inventory is not proof that a file does not exist. Version `0`, missing scan authority and unsupported rule versions do not finalize references, including legacy `InventoryPending` rows; no historical result is repaired.

Reason | Meaning
------ | -------
`ConfirmedPublishingLayoutFamily` | A unique same-scan, same-site independently discovered physical target satisfies the shared confirmed-family predicate, without retained Denied/Failed/Unknown discovery, assessment or type-source evidence
`InventoryPending` | Reference acquired; scan-wide relationship evaluation has not yet run
`ReferenceMetadataMissing` | The page URL field was absent or null
`ReferenceMetadataUnusable` | The field was not a supported native URL value or its URL could not be normalized safely; the description is never used to invent a URL
`ReferenceMetadataDenied`, `ReferenceMetadataFailed`, `ReferenceMetadataUnknown` | Acquisition was unavailable; later reference reads cannot erase this evidence. The existing discovery error columns retain acquisition exception details
`ReferenceMetadataConflict` | Repeated acquisition supplied different layout URLs; the earlier known URL and uncertainty are retained
`TargetNotDiscovered` | No matching physical inventory row in this scan/site; the reference URL remains known
`TargetAmbiguous` | Multiple eligible physical inventory rows share the locator; no target is guessed
`TargetNotPublishingLayoutFamily` | Available, resolved source-declared evidence proves that the selected target is outside the CLR family
`TargetTypeFamilyUnknown` | Target identity/ancestry has not proved membership or non-membership; inspect `PageTypeReason` and observation JSON in discovery.csv
`TargetTypeEvidenceUnavailable` | The target lacks the required source-declared evidence origin
`TargetTypeSourceDenied`, `TargetTypeSourceFailed`, `TargetTypeSourceUnknown`, `TargetTypeSourceUnavailable` | Target source acquisition is unavailable or unconfirmed; later successful observations cannot erase retained evidence
`TargetDiscoveryDenied`, `TargetDiscoveryFailed`, `TargetDiscoveryUnknown`, `TargetDiscoveryUnavailable` | Target discovery does not establish a successful relationship
`TargetAssessmentDenied`, `TargetAssessmentFailed`, `TargetAssessmentUnknown` | Target assessment retains unavailable evidence, even if its type family was confirmed
`NotEvaluated` | Safe migration/model default; no reference evaluation has been recorded

Acquisition reasons can be semicolon-separated when more than one observation is retained. Reference resolution never rewrites target acquisition states/reasons, content-page classification, `Layout`, Web Part counts or analysis results. The additive database migration defaults `LayoutUrl` to null and reference status/reason to `Unknown` / `NotEvaluated`. Previously stored or resumed historical results are not repaired or backfilled.

Historical reports may still contain `ConfirmedLayoutAsset`, `TargetPurposeUnavailable` or `TargetNotLayoutAsset` from the former ContentType-derived purpose rule. These are retained historical values, not proof of the corrected CLR-family relationship. No schema additions are needed for the revised reference rule.
