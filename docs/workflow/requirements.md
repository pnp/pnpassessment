# Requirements

This page lists the Workflow 2013 assessment specific requirements and options.

## Permission requirements

When using the Workflow 2013 module of the Microsoft 365 Assessment tool you do need to use a configured Azure AD application ([learn more here](../using-the-assessment-tool/setupauth.md)). The Microsoft 365 Assessment tool aims to be able to perform the Workflow 2013 assessment using minimal permissions, as listed below.

Authentication | Minimal
---------------| -------
Application | **Graph:** Sites.Read.All <br> **SharePoint:** Sites.Manage.All
Delegated | **Graph:** Sites.Read.All, User.Read <br> **SharePoint:** AllSites.Manage

## Command line arguments for starting an assessment

When starting the Workflow 2013 assessment you optionally opt out from the detailed analysis of the workflow via the `--workflowanalyze:false` argument. Doing so will mean skipping all the reporting and data that help you understand what actions are used in the workflow and how upgradable these are to Power Automate, hence it's not recommended to use this argument.

> [!Note]
> To learn more about starting an assessment checkout the Microsoft 365 Assessment tool [Start documentation](../using-the-assessment-tool/assess-start.md).
