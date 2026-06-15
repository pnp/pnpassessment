# classicpagewebparts.csv file details

## Summary

This csv file contains one row for every web part found on the assessed classic pages. It is the detailed inventory behind the `WebPartCount` / `MappingPercentage` columns of [classicpages.csv](csv-classicpages.md).

## Columns

The following columns are included:

Column|Description
------|-----------
PageUrl | Server-relative URL of the page the web part is on
WebPartIndex | Zero-based index of the web part within the page (document order)
WebPartType | The fully qualified web part type name
WebPartTypeShort | The short (class) name of the web part type
WebPartTitle | The web part's title
WebPartProperties | The web part's properties serialized as JSON (only populated when `--exportwebpartproperties` was specified)
ZoneId | The id of the web part zone the web part is in (web part / publishing pages)
Row | The row the web part is placed in
Column | The column the web part is placed in
Order | The order of the web part within its zone / cell
Hidden | True when the web part is hidden
IsClosed | True when the web part is closed
IsMappable | True when this web part type has a known modern mapping (i.e. it is present in the web part mapping model)
ScanId | Id of the assessment
SiteUrl | Fully qualified site collection URL
WebUrl | Relative URL of this web
