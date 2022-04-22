# syntexmodelusage.csv file details

## Summary

This csv file contains information about the Syntex content understanding models that have been assessed. Only will be populated whenever the Syntex Content Centers were in scope of the assessment.

## Columns

The following columns are included:

Column|Description
------|-----------
Classifier | Name of the document understanding or form processing model
TargetSiteId | Id of the site collection using the classifier
TargetWebId | Id of web using the classifier
TargetListId | Id of the list using the classifier
ClassifiedItemCount | Number of documents classified
NotProcessedItemCount | Number of documents not processed
AverageConfidenceScore | Average classifier confidence score
ScanId| Id of the assessment
SiteUrl | Fully qualified site collection URL
WebUrl | Relative URL of this web
