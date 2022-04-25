# Download the Microsoft 365 Assessment tool

Before being able to use the Microsoft 365 Assessment tool you first wil have to download it.

## Download the latest version

The Microsoft 365 Assessment tool versions are available via the [GitHub releases page](https://github.com/pnp/pnpassessment/releases) of the [repository](https://github.com/pnp/pnpassessment) hosting the open source Microsoft 365 Assessment tool. There are three flavors of the Microsoft 365 Assessment tool, a Windows version, macOS version and Linux version. It's recommended to always use the latest version.

When using the Microsoft 365 Assessment tool the tool outputs (reports, CSV files, logs) will be placed inside the folder containing the Microsoft 365 Assessment tool, hence it's recommended to put the downloaded Microsoft 365 Assessment tool somewhere inside a dedicated folder on your computer's file system. E.g. create a folder `c:\microsoft365assessment` and copy the `microsoft365-assessment.exe` file in that folder.

## Version updates

When you launch the Microsoft 365 Assessment tool it will check if there's a newer version and will notify you, downloading the newest version is a manual step and highly recommended whenever there is one.

> [!Important]
> When you download the new version you need to ensure the previous Microsoft 365 Assessment tool is not running anymore as otherwise you might get "file in use" errors while overwriting or the new version will still connected to the old running process. 

Below are some strategies to ensure the current Microsoft 365 Assessment tool is fully shutdown.

### Using the Microsoft 365 Assessment tool CLI

First check if there's no running assessment anymore via `microsoft365-assessment.exe status`, if OK then use `microsoft365-assessment.exe stop` to stop the running Microsoft 365 Assessment process.

### Using your OS's task manager

List all the running tasks and stop all `microsoft365-assessment.exe` processes on Windows or all `microsoft365-assessment` processes on macOS or Linux.

## Is it safe to just run a downloaded executable

The Microsoft 365 Assessment tool is an open source managed tool, but the release process is a Microsoft managed one. This means that tool changes are reviewed by Microsoft and that the shipped Windows versions of the tool are code signed by Microsoft, which prevents tampering with the binary file. Furthermore it's not required and therefore not recommended to run the Microsoft 365 Assessment tool using elevated privileges.

## Running on macOS and Linux

After copying the needed binary from the releases folder you need to mark the binary as executable via `sudo chmod +x microsoft365-assessment`. Once that's done you can use the Microsoft 365 Assessment tool, the Microsoft 365 Assessment tool binary itself contains all the needed dependencies (including the .NET 6 runtime).

While the Microsoft 365 Assessment tool can be used on macOS and Linux, there are some limitations on these platforms: the Power BI report generation will be skipped since Power BI Desktop is only is available for Windows. You however will be able to run an assessment and generate the needed CSV files containing the assessment results. If you later on want to generate the Power BI report for an assessment ran on Linux of macOS, you then can copy the assessment output folder (guid = assessment id, located in the folder containing the Microsoft 365 Assessment tool) to the folder on a Windows machine where you've put the Windows version of the Microsoft 365 Assessment tool. When you then use `microsoft365-assessment.exe report --id <assessment id>` the report will be generated and opened in Power BI Desktop.
