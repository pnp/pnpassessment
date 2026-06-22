# Microsoft 365 Assessment tool

The Microsoft 365 Assessment tool is an [open source community tool](https://github.com/pnp/pnpassessment) that provides customers with data to help them with various retirement and adoption scenarios.

## Getting started 🚀

The minimal steps to get started are:

Step | Description
-----|------------
[Download the tool](https://pnp.github.io/pnpassessment/using-the-assessment-tool/download.html) | Download the the Microsoft 365 Assessment tool for the OS you're using. The assessment tool versions can be found in the [releases](https://github.com/pnp/pnpassessment/releases) folder
[Configure authentication](https://pnp.github.io/pnpassessment/using-the-assessment-tool/setupauth.html) | Setup an Azure AD application that can be used to authenticate the Microsoft 365 Assessment tool to your tenant
[Run an assessment](https://pnp.github.io/pnpassessment/using-the-assessment-tool/assess.html) | Use the Microsoft 365 Assessment tool CLI to run an assessment: `microsoft365-assessment.exe --help` will show the available commands

Once you're ready to run an assessment you can choose any of the available modules, use below table to learn more about the specifics for a given module: you'll find information about to run the assessment for that module and a detailed description of the created report and CSV files. Currently supported modules are:

Module | Type | Description
-------|------|------------
[Classic page](https://pnp.github.io/pnpassessment/classic/readme.html) | Modernization | Helps you assess your tenant to understand where you're using classic pages (Web Part, Wiki, Blog and Publishing pages) and how ready they are for modernization. For each page it inventories the web parts, computes a modernization readiness percentage and the list of unmapped web parts, detects the page layout, flags home pages and uncustomized home pages, and captures usage statistics — rolling the results up into per-web and per-site-collection summaries. **Available as of version 1.15.0**
[InfoPath Forms Services](https://pnp.github.io/pnpassessment/infopath/readme.md) | Retirement | Helps you assess your tenant to understand where you're using InfoPath Forms Services and how upgradable those to alternative solutions. **Available as of version 1.5.0**
[SharePoint Add-Ins and Azure ACS principals](https://pnp.github.io/pnpassessment/addinsacs/readme.html) | Retirement | Helps you assess your tenant to understand where you're using SharePoint Add-Ins and Azure ACS principals. **Available as of version 1.6.0**
[SharePoint Alerts](https://pnp.github.io/pnpassessment/alerts/readme.html) | Retirement | Helps you assess your tenant to understand where you're using SharePoint Alerts. **Available as of version 1.11.0**

## I want to help 🙋‍♂️

If you want to join our team and help, then feel free to check the issue list for planned work or create an issue with suggested improvements. Check out our [Contribution guidance](https://pnp.github.io/pnpassessment/contributing/readme.html) to learn more.

## Supportability and SLA 💁🏾‍♀️

The Microsoft 365 Assessment tool in an open-source tool maintained by Microsoft and the community. When you do have a Premier support contract with Microsoft, you can use that route for opening a support ticket. When opening a support ticket is not possible for you, then please report any issues using the [issues list](https://github.com/pnp/pnpassessment/issues).

## Relationship with the "Modernization Scanner" ❓

As of version 1.15.0 the Microsoft 365 Assessment tool supports the **full classic page scan functionality** of the [Modernization Scanner](https://aka.ms/sharepoint/modernization/scanner) — classic page discovery and typing (Web Part, Wiki, Blog and Publishing pages), live web-part extraction, modernization readiness scoring, page-layout detection, usage statistics, and the per-web / per-site-collection summaries. The ported capability has been validated field-for-field against the legacy scanner on a live tenant across all four classic page types, with **significantly better performance and scale**. The [Modernization Scanner](https://aka.ms/sharepoint/modernization/scanner) should only be used if a needed module is not yet available as part of the Microsoft 365 Assessment tool.

## Community rocks, sharing is caring 💖

This project has adopted the [Microsoft Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct/). For more information see the [Code of Conduct FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or contact [opencode@microsoft.com](mailto:opencode@microsoft.com) with any additional questions or comments.
