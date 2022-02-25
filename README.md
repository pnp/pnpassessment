# Microsoft 365 Scanner
Microsoft 365 SharePoint scanning tool is an open source community tool that provides customers with data to help them with various deprecation and adoption scenarios. This tool is currently under development/testing, more features and especially more documentation will be added soon.

## Getting started 🚀

Minimal steps to run:
- Download the scanner for the OS you're using. Scanner versions can be found in the [releases](https://github.com/pnp/pnpscanning/tree/main/releases) folder
- Use the scanner CLI to operate it: `microsoft365-scanner.exe --help` will show the available commands

Common CLI calls are:

Task | CLI
-----|------
Start a new Syntex scan (application permissions) for a complete tenant | microsoft365-scanner.exe start --mode syntex --authmode application --tenant bertonline.sharepoint.com --applicationid c545f9ce-1c11-440b-812b-0b35217d9e83 --certpath "My&#124;CurrentUser&#124;b133d1cb4d19ce539986c7ac67de005481084c84"
Start a new Syntex deep scan (delegated permissions) for a set of site collections | microsoft365-scanner.exe start --mode Syntex --authmode interactive --tenant bertonline.sharepoint.com --siteslist "https://bertonline.sharepoint.com/sites/prov-1,https://bertonline.sharepoint.com/sites/temp2" --syntexdeepscan
List all the scans | microsoft365-scanner.exe list
Realtime status update of the running scans | microsoft365-scanner.exe status
Pause a running scan | microsoft365-scanner.exe pause --id &lt;scan id&gt;
Restart a paused or terminated scan | microsoft365-scanner.exe restart --id &lt;scan id&gt;  
Generate PowerBI report (includes CSV export) | microsoft365-scanner.exe report --id &lt;scan id&gt;
Export the gathered data to CSV | microsoft365-scanner.exe report --id &lt;scan id&gt; --mode CsvOnly --Path &lt;location to put CSV files&gt;

![scanner run](scannerrun.gif)

## I want to help 🙋‍♂️

If you want to join our team and help, then feel free to check the issue list for planned work or create an issue with sugggested improvements.

## Supportability and SLA 💁🏾‍♀️

This tool is an open-source and community provided tool backed by an active community supporting it. This is not a Microsoft provided tool, so there's no SLA or direct support for this open-source component from Microsoft. Please report any issues using the [issues list](https://github.com/pnp/pnpscannning/issues).

## FAQ

### Running on MacOS or Linux

The scanner can be used on MacOS and Linux except the PowerBI report generation as the PowerBI client only is available for Windows. After copying the needed binary from the [releases](https://github.com/pnp/pnpscanning/tree/main/releases) folder you need to make the binary as executable via `sudo chmod +x microsoft365-scanner`. Once that's done you can use the scanner, the scanner binary itself contains all the needed depencies (including the .NET 6 runtime). If you want to generate a PowerBI report for a scan run on Linux of MacOS you can copy the scan output folder (guid = scan id) located in the folder containing the binary to a folder on a Windows machine where you've put the Windows version of the scanner. When you then use `microsoft365-scanner.exe report --id <scan id>` the report will be generated and opened in PowerBI.

**Community rocks, sharing is caring!**

This project has adopted the [Microsoft Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct/). For more information see the [Code of Conduct FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or contact [opencode@microsoft.com](mailto:opencode@microsoft.com) with any additional questions or comments.
