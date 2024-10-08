# Requirements

This page lists the InfoPath Forms Services assessment specific requirements and options.

## Permission requirements

When using the InfoPath Forms Services module of the Microsoft 365 Assessment tool you do need to use a configured Entra application ([learn more here](../using-the-assessment-tool/setupauth.md)). The Microsoft 365 Assessment tool aims to be able to perform the InfoPath Forms Services assessment using minimal permissions, as listed below.

Authentication | Minimal
---------------| -------
Application | **Graph:** Sites.Read.All <br> **SharePoint:** Sites.Read.All
Delegated | **Graph:** Sites.Read.All, User.Read <br> **SharePoint:** AllSites.Read

## Command line arguments for starting an assessment

There are no specific command line arguments when starting the InfoPath Forms Services assessment.

> [!Note]
> To learn more about starting an assessment checkout the Microsoft 365 Assessment tool [Start documentation](../using-the-assessment-tool/assess-start.md).
