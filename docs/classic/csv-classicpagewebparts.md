# classicpagewebparts.csv file details

## Summary

This csv file contains one row for every web part found on the assessed classic pages. It is the detailed inventory behind the `WebPartCount` / `MappingPercentage` columns of [classicpages.csv](csv-classicpages.md).

Confirmed Page Layout assets are excluded before content-page extraction in new scans. Their bodies and Web Parts are not combined with a referring Publishing page's analysis. The assets remain in [discovery.csv](csv-discovery.md); upgrading a database leaves historical Web Part rows unchanged.

## Columns

The following columns are included:

Column|Description
------|-----------
PageUrl | Server-relative URL of the page the web part is on
WebPartIndex | Zero-based index of the web part within the page (document order)
WebPartType | The fully qualified web part type name
WebPartTypeShort | The web part type name without its assembly qualification (the namespace is retained)
WebPartTitle | The web part's title
WebPartProperties | The web part's properties serialized as JSON (only populated when `--exportwebpartproperties` was specified)
ZoneId | The id of the web part zone the web part is in (web part / publishing pages)
Row | The row the web part is placed in
Column | The column the web part is placed in
Order | The order of the web part within its zone / cell
Hidden | True when the web part is hidden
IsClosed | True when the web part is closed
IsMappable | True when this web part type has a modern mapping; presence in the mapping model alone is not sufficient
ScanId | Id of the assessment
SiteUrl | Fully qualified site collection URL
WebUrl | Relative URL of this web
