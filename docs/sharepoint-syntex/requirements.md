# Requirements

This page lists the SharePoint Syntex adoption assessment specific requirements and options.

## Permission requirements

When using the SharePoint Syntex adoption module of the Microsoft 365 Assessment tool you do need to use a configured Azure AD application ([learn more here](../using-the-assessment-tool/setupauth.md)). The Microsoft 365 Assessment tool aims to be able to perform the SharePoint Syntex adoption assessment using minimal read permissions, but for a full assessment the optimal permissions are required.

Authentication | Minimal | Optimal | Details
---------------| --------|---------|--------
Application | **Graph:** Sites.Read.All <br> **SharePoint:** Sites.Read.All | **Graph:** Sites.Read.All <br> **SharePoint:** Sites.FullControl.All | When using the `--syntexfull` argument the assessment tool will use the search APIs to count how many documents use a given content type and how many retention labels there are applied on a list, and search in combination with application permissions requires Sites.FullControl.All. The assessment tool will also check if a library uses workflow 2013 and this requires the Sites.Manage.All or higher permission role
Delegated | **Graph:** Sites.Read.All, User.Read <br> **SharePoint:** AllSites.Read | **Graph:** Sites.Read.All, User.Read <br> **SharePoint:** AllSites.Manage | The assessment tool will check if a library uses workflow 2013 and this requires the AllSites.Manage or higher permission scope

## Command line arguments for starting an assessment

When starting the SharePoint Syntex adoption assessment it's recommended to use the `--syntexfull` argument, adding this argument will make the assessment use search to gather exact file counts per content type and retention label counts. This however also requires that your Azure AD application is correctly configured to allow this as was explained in previous chapter.

> [!Note]
> To learn more about starting an assessment checkout the Microsoft 365 Assessment tool [Start documentation](../using-the-assessment-tool/assess-start.md).
