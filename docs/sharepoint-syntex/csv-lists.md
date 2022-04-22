# syntexlists.csv file details

## Summary

This csv file contains information about the libraries that have been assessed. One row per library assessed library is collected.

## Columns

The following columns are included:

Column|Description
------|-----------
ListServerRelativeUrl| Server relative URL of this library
Title| Title of the library
ListId| Id of the library
ListTemplate| Numeric template id used by the library
ListTemplateString| Text template name used by the library
AllowContentTypes| Does the library allow content types?
ContentTypeCount| Number of content types configured for the library
FieldCount| Number of custom fields configured for the library
ListExperienceOptions| List experience setting (`Auto`, `ClassicExperience`, `NewExperience`)
WorkflowInstanceCount| Number of workflow 2013 instances connected to this library (note: only available when the required permissions were granted)
FlowInstanceCount| Number of Power Automate (Flow) instances connected to this library (note: not yet implemented)
RetentionLabelCount| Number of retention labels used inside this library (note: only available when a full scan was done)
ItemCount| Number of items in this library, is count of files and folders
FolderCount| Folder count in this library (note: only available when a full scan was done)
DocumentCount| Number of documents in this library (note: only 100% accurate when a full scan was done, without full scan this number matches the itemcount as estimated value)
AverageDocumentsPerFolder| Average number of documents per folder (note: only available when a full scan was done)
LibrarySize| Library size: `small` (less than 100), `medium` (less than 1000) or `large` (1000 or more)
UsesCustomColumns| Does the library use custom columns?
Created| When was the library created?
LastChanged| When was the library last changed?
LastChangedYear| Last changed year
LastChangedMonth| Last changed month number
LastChangedMonthString| Last changed month name
LastChangedQuarter| Last changed quarter
ScanId| Id of the assessment
SiteUrl | Fully qualified site collection URL
WebUrl | Relative URL of this web
