# classicinfopath.csv file details

## Summary

This csv file contains information about the InfoPath Forms Services usage that has been assessed.

## Columns

The following columns are included:

Column|Description
------|-----------
ListUrl | If the InfoPath usage is scoped to a list or library this contains the url of that list or library
ListTitle | If the InfoPath usage is scoped to a list or library this contains the title of that list or library
ListId | If the InfoPath usage is scoped to a list or library this contains the id of that list or library
InfoPathUsage | Indicates how InfoPath is used: when `FormLibrary` an InfoPath form is used to collect data which is stored as InfoPath XML in a form library. When `CustomForm` an InfoPath form is used to customize the list forms and finally when `ContentType` the InfoPath form is linked to a content type
InfoPathTemplate | The name of the InfoPath form template (the .xsn file being used)
Enabled | Always true in this report
ItemCount | The amount of items/files in the list or library having InfoPath usage. Lists or form libraries containing zero or a few items are often less important to migrate
LastItemUserModifiedDate | When was the last user triggered change for an item in the list/library that uses this InfoPath
ScanId | Id of the assessment
SiteUrl | Fully qualified site collection URL
WebUrl | Relative URL of this web
