# Requirements

This page lists the SharePoint Add-In and Azure ACS assessment specific requirements and options.

## Permission requirements

When using the SharePoint Add-In and Azure ACS module of the Microsoft 365 Assessment tool you do need to use a configured Azure AD application ([learn more here](../using-the-assessment-tool/setupauth.md)). The Microsoft 365 Assessment tool aims to be able to perform the SharePoint Add-In and Azure ACS assessment using minimal permissions, as listed below.

Authentication | Minimal
---------------| -------
Application | **Graph:** Sites.Read.Al, Application.Read.All <br> **SharePoint:** Sites.Read.All
Delegated | **Graph:** Sites.Read.All, Application.Read.All, User.Read <br> **SharePoint:** AllSites.Read

## Command line arguments for starting an assessment

There are no specific command line arguments when starting the SharePoint Add-In and Azure ACS assessment.

> [!Note]
> To learn more about starting an assessment checkout the Microsoft 365 Assessment tool [Start documentation](../using-the-assessment-tool/assess-start.md).
