# classicacsprincipalsitescopedpermissions.csv file details

## Summary

This csv file contains information about the site collection, web or list scoped permissions the assessed Azure ACS principals have.

## Columns

The following columns are included:

Column|Description
------|-----------
ScanId | Id of the assessment
AppIdentifier | The Azure ACS application principal id that's being used by applications
ServerRelativeUrl | Server relative URL of the web where the listed Azure ACS principal has been granted rights
SiteId | Id of the site collection where the Azure ACS principal was granted specific access
WebId | Id of the web where the Azure ACS principal was granted specific access
ListId | Id of the list where the Azure ACS principal was granted specific access
Right | Permission scope that was granted, possible values are: `Guest`, `Read`, `Write`, `Manage` and `FullControl`
RemediationCode | Link to remediation code
