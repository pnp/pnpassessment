# Reports

## Report updates

- Generate existing report with CSV files
- Ensure you're not signed in with your MSFT account
- Edit report, add new CSV files as data source if needed
- Save as PBIX

## Integrate updated report

- Copy the used CSV files and updated PBIX to the relevant folder in `Q:\github\pnpassessment\src\PnP.Scanning\Reports\<assessment module>`
- Click on "Transform data" -> "Data source settings"
- Update each CSV file to point to the CSV file located under `Q:\github\pnpassessment\src\PnP.Scanning\Reports\<assessment module>`
- Click on "Apply Changes" 
- Check the report and if all looks good save the PBIX again
- Save the report as PBIT overwriting the current PBIT file
- Copy the created PBIT file to the respective code location (e.g. `Q:\github\pnpassessment\src\PnP.Scanning\PnP.Scanning.Core\Scanners\<assessment module>`)