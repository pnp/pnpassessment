# Scans.csv file details

## Summary

This table contains a single row describing information about the assessment itself.

## Columns

The following columns are included:

Column | Description
-------|------------
ScanId | Id of the assessment
StartDate | Start date of the assessment
EndDate | Date when the assessment finished
Status | Status of the assessment
PreScanStatus | Status of the pre-assessment run
PostScanStatus | Status of the post-assessment run
Version | Version of the Microsoft 365 Assessent tool used
CLIMode | Used assessment mode via CLI
CLITenant | Tenant specified via CLI
CLITenantId | Tenant id provided via CLI
CLIEnvironment | Used environment (see [here](../using-the-assessment-tool/configuration.md) for more details on environment)
CLISiteList | Was a sites list used to scope the assessment?
CLISiteFile | Was a sites file used to scope the assessment?
CLIAuthMode | Authentication mode used for the assessment
CLIApplicationId | Azure AD application ID used
CLICertPath | Was a certificate path used?
CLICertFile | Was a certificate file used?
CLICertFilePassword | Encrypted PFX file password (see [here](../using-the-assessment-tool/assess-start.md#authentication-configuration) for more details)
CLIThreads | Number of parallel operations used by the assessment
