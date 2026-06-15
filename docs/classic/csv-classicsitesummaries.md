# classicsitesummaries.csv file details

## Summary

This csv file contains one row per assessed site collection with roll-up information. It is shared by all classic assessment components; this page documents the columns relevant to the classic **pages** assessment (page counts and page-modernization-readiness roll-ups, aggregated from all webs in the site collection). The page-readiness columns are computed only over pages that actually carry web parts.

## Columns

The following page-relevant columns are included (the file also contains roll-up columns for the other classic components such as lists, workflows and add-ins):

Column|Description
------|-----------
RootWebTemplate | The template of the site collection's root web
SubWebCount | Number of sub webs in the site collection
ClassicPages | Total number of classic pages found across the site collection
ClassicWikiPages | Number of classic wiki pages
ClassicASPXPages | Number of classic ASPX pages
ClassicBlogPages | Number of classic blog pages
ClassicWebPartPages | Number of classic web part pages
ClassicPublishingPages | Number of classic publishing pages
ModernPages | Number of modern pages found across the site collection
PagesWithWebParts | Number of classic pages that carry at least one web part
MappableWebPartPages | Number of pages (with web parts) whose web parts are fully mappable (mapping percentage of 100)
UnmappedWebPartPages | Number of pages (with web parts) that have at least one unmapped web part (mapping percentage below 100)
AvgMappingPercentage | The average mapping percentage across the pages that carry web parts
UncustomizedHomePages | Number of uncustomized (default) home pages found across the site collection
AggregatedRemediationCodes | The aggregated remediation codes for the site collection
ScanId | Id of the assessment
SiteUrl | Fully qualified site collection URL
