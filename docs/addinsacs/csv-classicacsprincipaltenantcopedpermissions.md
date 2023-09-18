# classicacsprincipaltenantcopedpermissions.csv file details

## Summary

This csv file contains information about the tenant scoped permissions the assessed Azure ACS principals have.

## Columns

The following columns are included:

Column|Description
------|-----------
ScanId | Id of the assessment
AppIdentifier | The Azure ACS application principal id that's being used by applications
ProductFeature | The product feature name for the permission scopes, possible values are `Taxonomy`, `Social`, `ProjectServer`, `Search`, `BcsConnection` and `Content`
Scope | The scope of the permission, e.g. `content/tenant`, `search`, `social/tenant`, `taxonomy`, `projectserver/projects`
Right | Permission scope that was granted, possible values are: `Guest`, `Read`, `Write`, `Manage`, `FullControl`, `QueryAsUserIgnoreAppPrincipal`, `SubmitStatus` and `Elevate`. See [Azure ACS permission scopes](https://learn.microsoft.com/en-us/sharepoint/dev/sp-add-ins/add-in-permissions-in-sharepoint) for more details.
ResourceId | The specific resource id given to the app. For example, if the permission given to the specific Project Server project, then this is the project id.
RemediationCode | Link to remediation code
