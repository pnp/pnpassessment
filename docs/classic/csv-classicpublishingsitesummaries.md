# classicpublishingsitesummaries.csv file details

## Summary

This csv file contains one row per **publishing portal** (site collection that hosts one or more classic publishing webs). It rolls the publishing webs and publishing pages of a site collection up into a single line, providing a portal-level view of the master pages and page layouts in use. It is the equivalent of the legacy SharePoint Modernization Scanner's `ModernizationPublishingSiteScanResults.csv`.

A site collection only appears in this file when it contains at least one publishing web — a web on a publishing template, or a web that carries at least one classic publishing page (publishing feature enabled on a non-publishing template).

For scans with recorded `PublishingLayoutRuleVersion = 1`, `NumberOfPages` counts Publishing content pages, excluding confirmed source-declared `PublishingLayoutPage`-family files. ContentType or layout selection alone does not cause exclusion. `UsedPageLayouts` contains the content pages' existing friendly `Layout` names, not an asset inventory or resolved-reference count. A content page with an unresolved layout reference still contributes to this report. Use `LayoutUrl` and reference evidence in [classicpages.csv](csv-classicpages.md) to relate a page to the physical inventory in [discovery.csv](csv-discovery.md). Historical summaries are not repaired on upgrade.

## Columns

Column|Description
------|-----------
ScanId | Id of the assessment
SiteUrl | Fully qualified site collection URL of the publishing portal
NumberOfWebs | Number of publishing webs in the portal
NumberOfPages | Total number of classic publishing pages across the portal's webs
UsedSiteMasterPages | Comma-separated, de-duplicated list of the custom (site) master pages used across the portal's publishing webs. Populated only when the Extensibility component is included in the assessment
UsedSystemMasterPages | Comma-separated, de-duplicated list of the system master pages used across the portal's publishing webs. Populated only when the Extensibility component is included in the assessment
UsedPageLayouts | Comma-separated, de-duplicated list of the page layouts used by the portal's publishing pages
LastPageUpdateDate | The most recent modification date across the portal's publishing pages (empty when no publishing pages were scanned)
