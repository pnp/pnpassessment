# classicpages.csv file details

## Summary

This csv file contains one row for every classic page (wiki, web part, publishing, blog or ASPX page) that was discovered and assessed, including its page-modernization-readiness columns.

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
