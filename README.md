# Microsoft 365 Assessment tool

The Microsoft 365 Assessment tool is an [open source community tool](https://github.com/pnp/pnpassessment) that provides customers with data to help them with various deprecation and adoption scenarios. Currently the tool supports a [SharePoint Syntex adoption](https://pnp.github.io/pnpassessment/sharepoint-syntex/readme.html) and [Workflow 2013](https://pnp.github.io/pnpassessment/workflow/readme.html) module but additional modules are under development.

## Getting started 🚀

The minimal steps to get started are:

Step | Description
-----|------------
[Download the tool](https://pnp.github.io/pnpassessment/using-the-assessment-tool/download.html) | Download the the Microsoft 365 Assessment tool for the OS you're using. The assessment tool versions can be found in the [releases](https://github.com/pnp/pnpassessment/releases) folder
[Configure authentication](https://pnp.github.io/pnpassessment/using-the-assessment-tool/setupauth.html) | Setup an Azure AD application that can be used to authenticate the Microsoft 365 Assessment tool to your tenant
[Run an assessment](https://pnp.github.io/pnpassessment/using-the-assessment-tool/assess.html) | Use the Microsoft 365 Assessment tool CLI to run an assessment: `microsoft365-assessment.exe --help` will show the available commands

Once you're ready to run an assessment you can choose any of the available modules, use the top navigation to learn more about the specifics for a given module: you'll find information about to run the assessment for that module and a detailed description of the created report and CSV files. Currently supported modules are:

Module | Type | Description
-------|------|------------
[SharePoint Syntex](https://pnp.github.io/pnpassessment/sharepoint-syntex/readme.html) | Adoption | Helps you assess your tenant to understand where using SharePoint Syntex will bring value to your organization
[Workflow 2013](https://pnp.github.io/pnpassessment/workflow/readme.html) | Deprecation | Helps you assess your tenant to understand where you're using Workflow 2013 and how upgradable those workflows are to Power Automate. **Available as of pre-release version 1.0.1**

## I want to help 🙋‍♂️

If you want to join our team and help, then feel free to check the issue list for planned work or create an issue with suggested improvements. Check out our [Contribution guidance](https://pnp.github.io/pnpassessment/contributing/readme.html) to learn more.

## Supportability and SLA 💁🏾‍♀️

The Microsoft 365 Assessment tool in an open-source tool maintained by Microsoft and the community. When you do have a Premier support contract with Microsoft, you can use that route for opening a support ticket. When opening a support ticket is not possible for you, then please report any issues using the [issues list](https://github.com/pnp/pnpassessment/issues).

## Relationship with the "Modernization Scanner" ❓

Overtime the Microsoft 365 Assessment tool will replace the relevant [Modernization Scanner](https://aka.ms/sharepoint/modernization/scanner) modules, for the time being the [Modernization Scanner](https://aka.ms/sharepoint/modernization/scanner) should be used if the needed module is not available as part of the the Microsoft 365 Assessment tool.

## Community rocks, sharing is caring 💖

This project has adopted the [Microsoft Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct/). For more information see the [Code of Conduct FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or contact [opencode@microsoft.com](mailto:opencode@microsoft.com) with any additional questions or comments.
