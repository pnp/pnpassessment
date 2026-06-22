# Run a classic pages assessment

Running the classic pages assessment is just like running any other module of the Microsoft 365 Assessment tool: you use the CLI with the `Start` action to launch an assessment. By specifying the `--mode` to be `Classic` (and including the `Pages` component) the Microsoft 365 Assessment tool will run the classic pages assessment for you. This page provides you with a quick start and links to the relevant Microsoft 365 Assessment tool documentation for more details.

> [!NOTE]
> The classic pages assessment is available as of **version 1.15.0** of the Microsoft 365 Assessment tool. Within a Classic assessment the Azure ACS and SharePoint Add-Ins components are skipped (they are provided by the dedicated [`--mode AddInsACS`](../addinsacs/readme.md) module).

## Quick start

### Download the Microsoft 365 Assessment tool

The Microsoft 365 Assessment tool must first be downloaded from https://github.com/pnp/pnpassessment/releases. More details can be found in the [download](../using-the-assessment-tool/download.md) documentation.

### Ensure you've an Entra application setup

The Microsoft 365 Assessment tool requires an Entra application for authenticating to SharePoint. More details in the [authentication](../using-the-assessment-tool/setupauth.md) documentation.

### Start assessment

Below are some quick start samples that show how to run a classic pages assessment. More details on the `Start` action can be found in the [Microsoft 365 Assessment tool Start documentation](../using-the-assessment-tool/assess-start.md).

Task | CLI
-----|------
Start a new classic pages assessment (application permissions) for a complete tenant | microsoft365-assessment.exe start --mode Classic --classicinclude Pages --authmode application <br> --tenant bertonline.sharepoint.com --applicationid c545f9ce-1c11-440b-812b-0b35217d9e83 <br> --certpath "My&#124;CurrentUser&#124;b133d1cb4d19ce539986c7ac67de005481084c84"
Start a classic pages assessment for a set of site collections | microsoft365-assessment.exe start --mode Classic --classicinclude Pages --authmode interactive <br> --tenant bertonline.sharepoint.com --applicationid c545f9ce-1c11-440b-812b-0b35217d9e83 <br> --siteslist "https://bertonline.sharepoint.com/sites/ussales,https://bertonline.sharepoint.com/sites/europesales"
Assess only the home page of each web, exporting web part properties | microsoft365-assessment.exe start --mode Classic --classicinclude Pages --homepageonly --exportwebpartproperties <br> --authmode application --tenant bertonline.sharepoint.com --applicationid c545f9ce-1c11-440b-812b-0b35217d9e83 <br> --certpath "My&#124;CurrentUser&#124;b133d1cb4d19ce539986c7ac67de005481084c84"

See the [requirements](requirements.md) page for the full list of classic page-scan arguments (`--exportwebpartproperties`, `--skipusageinformation`, `--skipuserinformation`, `--homepageonly`).

### Live status updates

Once an assessment is launched you'd typically followup on it's status via the `Status` action. Below is a quick start, more details can be found in the [Microsoft 365 Assessment tool operations documentation](../using-the-assessment-tool/assess-operations.md#getting-a-live-status-overview-of-a-running-assessment).

Task | CLI
-----|------
Realtime status update of the running assessments | microsoft365-assessment.exe status

### Generate report

When an assessment has finished you can continue with the next step and that's generating the Power BI report by using the `Report` action. Below is a quick start, more details can be found in the [Microsoft 365 Assessment tool Report documentation](../using-the-assessment-tool/assess-report.md).

Task | CLI
-----|------
Generate Power BI report (includes CSV export) in the default location | microsoft365-assessment.exe report --id 22989c75-f08f-4af9-8857-6f19e333d6d3
Export the gathered data as CSV files in a custom location | microsoft365-assessment.exe report --id 22989c75-f08f-4af9-8857-6f19e333d6d3 <br> --mode CsvOnly --path "c:\reports"

[!INCLUDE [Clarify the --id parameter](./../fragments/clarify-id-parameter.md)]

To better understand the generated Power BI report and accompanying CSV files use the nodes in the left navigation.
