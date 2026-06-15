# classicwebpartunique.csv file details

## Summary

This csv file contains one row for every unique web part type encountered across the whole assessment, together with whether that type is known to the modern web part mapping model and on how many pages it was found. It is useful to quickly understand which web part types are driving your unmapped (not-yet-modernizable) pages.

## Columns

The following columns are included:

Column|Description
------|-----------
WebPartType | The fully qualified web part type name
InMappingFile | True when this web part type is present in the web part mapping model (i.e. it has a known modern mapping)
PageCount | The number of distinct pages this web part type was found on
ScanId | Id of the assessment
SiteUrl | Fully qualified site collection URL
WebUrl | Relative URL of this web
