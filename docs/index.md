# Microsoft 365 Assessment tool

The Microsoft 365 Assessment tool is an [open source tool](https://github.com/pnp/pnpassessment) that provides customers with data to help them with various deprecation and adoption scenarios. Currently the tool supports a [SharePoint Syntex adoption](sharepoint-syntex/readme.md) and [Workflow 2013](workflow/readme.md) module but additional modules are under development.

## Getting started 🚀

The minimal steps to get started are:

Step | Description
-----|------------
[Download the tool](using-the-assessment-tool/download.md) | Download the the Microsoft 365 Assessment tool for the OS you're using. The assessment tool versions can be found in the [releases](https://github.com/pnp/pnpassessment/releases) folder
[Configure authentication](using-the-assessment-tool/setupauth.md) | Setup an Azure AD application that can be used to authenticate the Microsoft 365 Assessment tool to your tenant
[Run an assessment](using-the-assessment-tool/assess.md) | Use the Microsoft 365 Assessment tool CLI to run an assessment: `microsoft365-assessment.exe --help` will show the available commands

Once you're ready to run an assessment you can choose any of the available modules, use the top navigation to learn more about the specifics for a given module: you'll find information about to run the assessment for that module and a detailed description of the created report and CSV files. Currently supported assessments are:

[!INCLUDE [Assessment overview](fragments/supportedassessments.md)]

## I want to help 🙋‍♂️

If you want to join our team and help, then feel free to check the issue list for planned work or create an issue with suggested improvements. Check out our [Contribution guidance](contributing/readme.md) to learn more.

## Supportability and SLA 💁🏾‍♀️

The Microsoft 365 Assessment tool in an open-source tool maintained by Microsoft and the community. When you do have a Premier support contract with Microsoft, you can use that route for opening a support ticket. When opening a support ticket is not possible for you, then please report any issues using the [issues list](https://github.com/pnp/pnpassessment/issues).

## Relationship with the "Modernization Scanner" ❓

Overtime the Microsoft 365 Assessment tool will replace the relevant [Modernization Scanner](https://aka.ms/sharepoint/modernization/scanner) modules, for the time being the [Modernization Scanner](https://aka.ms/sharepoint/modernization/scanner) should be used if the needed module is not available as part of the the Microsoft 365 Assessment tool.

## Community rocks, sharing is caring 💖

This project has adopted the [Microsoft Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct/). For more information see the [Code of Conduct FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or contact [opencode@microsoft.com](mailto:opencode@microsoft.com) with any additional questions or comments.
