# classicpages.csv file details

## Summary

This csv file contains one row for every classic page (wiki, web part, publishing, blog or ASPX page) that was discovered and assessed, including its page-modernization-readiness columns.

A page whose list item could not be read — a web's root `default.aspx` and other files that have no backing list item at all — is still assessed from its file alone: its web parts are read through the file's web part manager. The columns that come from the list item (`ListUrl`, `ListTitle`, `ListId`, `ListItemId`, `ModifiedAt`, `ModifiedBy`) are then empty rather than filled with a stand-in, and [discovery.csv](csv-discovery.md) records why in `ErrorCodes`.

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
CustomizedPageStatus | SharePoint customization status of the page file: `Uncustomized` (ghosted), `Customized` (unghosted), or `None` (no customization classification). Empty when unavailable; never infer ghosting from an empty value or `None`.
LayoutUrl | URL of the publishing page's referenced layout file, as stored in `PublishingPageLayout`. Empty for non-publishing pages or when the reference is unavailable.
LayoutCustomizedPageStatus | SharePoint customization status of the referenced publishing layout file: `Uncustomized`, `Customized`, or `None`. Populated during post-scanning from referenced discovery rows; missing statuses (including `--homepageonly`) use a cached fallback with SDK retries. Conflicting observations retain the latest known status and record `changed_during_scan` in `discovery.csv`. Empty for non-publishing pages or when unavailable. Layout failures are recorded as `PageLayoutMetadata` without changing the page's `AssessmentStatus`.
HomePage | True when this page is the web's home (welcome) page, False when it is not, or empty when the web's welcome page could not be resolved
UncustomizedHomePage | True when this home page is still the default, uncustomized home page (only meaningful when `HomePage` is true)
ModifiedBy | Display name of the user who last modified the page (empty when `--skipuserinformation` was specified)
WebPartCount | Number of web parts found on the page
MappingPercentage | Percentage (0-100) of the page's web parts that have a known modern mapping. A page with no web parts is reported as 100%, except for a page assessed from its file alone: only its web part zones were read, so an empty result is left uncomputed rather than scored as fully mappable. An uncomputed value is written as 0 — read it together with `WebPartCount`: `WebPartCount` 0 with 0% means the rollup was withheld, while any `WebPartCount` above 0 with 0% means the page's web parts genuinely have no known modern mapping
UnmappedWebParts | Comma-separated list of the (short) web part type names on the page that do not have a known modern mapping
RemediationCode | The remediation code for this page type (`CP1`-`CP5`)
ScanId | Id of the assessment
SiteUrl | Fully qualified site collection URL
WebUrl | Relative URL of this web
