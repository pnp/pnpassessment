# classicacsprincipals.csv file details

## Summary

This csv file contains information about the SharePoint Azure ACS principals that have been assessed.

## Columns

The following columns are included:

Column|Description
------|-----------
ScanId | Id of the assessment
AppIdentifier | The Azure ACS application principal id that's being used by applications
HasExpired | If we can find the ACS principal's secrets and the most recent is expired then this is set to true, false otherwise
HasTenantScopedPermissions | Was this Azure ACS principal configured with permissions that apply to the whole tenant?
HasSiteCollectionScopedPermissions | Was this Azure ACS principal configured with permissions for one or more specific site collections, webs or lists?
Title | Title of the Azure ACS principal
AllowAppOnly | Can this Azure ACS principal be used to grant an application access without a user (so called app-only or application permissions)
AppId | The id of the Azure ACS principal
RedirectUri | The configured redirect URI
AppDomains | The configured application domain
ValidUntil | If we can find the ACS principal's secrets this shows the most recent validity
RemediationCode | Link to remediation code

[!INCLUDE [Clarify the expired acs field](./../fragments/clarify-acs-details.md)]