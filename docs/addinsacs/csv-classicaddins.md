# classicaddins.csv file details

## Summary

This csv file contains information about the SharePoint Add-In usage that has been assessed.

## Columns

The following columns are included:

Column|Description
------|-----------
AppInstanceId | The id of the Add-In installation
Title | Title of the Add-In
Type | Type of the Add-In, either `SharePoint Hosted` or `Provider Hosted`
HasExpired | When type is `Provider Hosted` and the Add-In was not acquired from the marketplace this field is true in case the Add-In has no valid Azure ACS principal secret anymore
AppSource | Indicates where the Add-In came from, possible values are: `Marketplace`, `CorporateCatalog`, `DeveloperSite`, `ObjectModel`, `RemoteObjectModel`, `SiteCollectionCorporateCatalog` and `InvalidSource`
AppWebFullUrl | The full url of the app web. The SharePoint components are generally in a special child web of the host web called the app web. The app web will be created during install the Add-In
AppWebId | Id of the app web
AssetId | The id of the app in the marketplace, this will be empty for Add-Ins installed from elsewhere
CreationTime | Date and time when the Add-In was installed
InstalledBy | Name of the user who installed the Add-In
InstalledSiteId | Site collection id where the Add-In installed
InstalledWebId | Site id where the Add-in installed
InstalledWebFullUrl | Site url where the Add-In installed
InstalledWebName | Site name where the Add-In installed
CurrentSiteId | Site collection id of current site
CurrentWebId | Site id of current site
CurrentWebFullUrl | Site url of current site
CurrentWebName | Site name of current site
LaunchUrl | The Add-In's launch page address
LicensePurchaseTime | Date and time when the Add-In license was purchased
PurchaserIdentity | Identity of the person acquiring the Add-In license
Locale | Locale of the site where the Add-In was installed
ProductId | The global unique id of the Add-In. It is same for all tenants
Status | The status of current Add-In, possible values are: `Installed`, `Installing`, `Uninstalling`, `Upgrading`, `Recycling`, `InvalidStatus`, `Canceling`, `Initialized`, `UpgradeCanceling`, `Disabling`, `Disabled`, `SecretRolling`, `Restoring` and `RestoreCanceling`
TenantAppData | After the Add-In installed in the tenant app catalog site it could be enabled for tenant level usage. This data indicates the conditions how to filter the sites. If this field is not empty, it means this Add-In was installed in the tenant app catalog site, deployed to tenant level, and current site matches the conditions. For more information, see [Tenancies and deployment scopes for SharePoint Add-ins](https://learn.microsoft.com/en-us/sharepoint/dev/sp-add-ins/tenancies-and-deployment-scopes-for-sharepoint-add-ins)
TenantAppDataUpdateTime | The tenant app data update time
AppIdentifier | The application principal id that's being used by the Add-In
RemediationCode | Link to remediation code
ScanId | Id of the assessment
SiteUrl | Fully qualified site collection URL
WebUrl | Relative URL of this web
