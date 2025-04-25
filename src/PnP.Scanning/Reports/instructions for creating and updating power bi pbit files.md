# Guidelines on report development for the Assessment tool

Here are the instuctions for creating and updating the Power BI reports which are included in the assessment tool.

**Important:** All report files must live in a subfolder under folder Q:\github\pnpassessment\src\PnP.Scanning\Reports ==> this path is hardcoded used in the code at the moment. If you change this path then please do this for all reports.


## Start developing a report for a new assessment module

Basic instructions to get started. Use the existing Power BI reports for inspiration.

1. Build the data export functionality in ReportManager.ExportReportDataAsync
2. Run the new assessment with as complete as possible scope 
3. Export the data to CSV, keep the default comma delimiter
4. Copy the export CSV data to a module folder under Q:\github\pnpassessment\src\PnP.Scanning\Reports
5. Create a new Power BI report in the module folder named "{modulename}AssessmentReport.pbix"
6. Import the export CSV files in the report, built the data model and design the report
7. Save as "{modulename}AssessmentReport.pbix"


## Convert developed Power BI report (PBIX) to PBIT and include in assessment tool

Since MSFT enforces a sensitivity setting on each created Power BI report we need to strip this from the pbit file we use as template. Follow these steps:

1. Verify that sensitivity is set to "Public"
2. Save as PBIT file, no description needed
3. Close Power BI, choose "Don't save"
4. Open the exported pbit file in 7-zip
5. Navigate to the docProps folder and right click custom.xml and choose Edit
6. Remove all the ```<Property...></property>``` nodes, this is what's left:

```xml
<?xml version="1.0" encoding="utf-8" standalone="yes"?><Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/custom-properties" xmlns:vt="http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes"></Properties>
```

7. Close notepad with custom.xml, click on Save to persist the changes in the pbit zip file
8. Click OK on the "do you want to update in archive" question
9. Close 7-zip
10. Copy the pbit file into the respective scanner folder (e.g. Q:\github\pnpassessment\src\PnP.Scanning\PnP.Scanning.Core\Scanners\Classic)
11. In Visual Studio set the Build Action the copied pbit file to "Embedded resource", copy to output directory is set to "Do not copy"
12. Update the code in ReportManager.CreatePowerBiReportAsync to use the added pbit file

## IMPORTANT: Dealing with DateTime values

To ensure datetime values are handled correctly they should be imported as Text from the CSV file. Note this error will only show up when the provided date does not match a us format:
1. Click on "Transform Data"
2. For the imported tables verify that type is set to Text, e.g. `{"StartDate", type text}` and click on "Apply and close"
3. Go to the "Model view" (3rd icon left side)
4. Select the fields which represent dates (they're now listed as regular text field) and change "Data type" to "*14/03/2001 13:30:55 (General Date)"

## IMPORTANT: Adding a WebURLAbsolute caculated field to the core data table

The core data table (e.g. Alerts) needs to be linked to the webs table. This is best done by adding a new calculated column using formula [SiteUrl] & [WebUrl] and then name the column WebURLAbsolute
Also add ScanDurationInMinutes = DATEDIFF(scans[StartDate],scans[EndDate],MINUTE) as new columns to the scans table

Related, ensure you copy the data model from one of the other reports when you create a new one