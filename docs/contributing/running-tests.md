# Running the tests

The engine (`PnP.Scanning`) has a test project, `PnP.Scanning.Core.Tests` (xUnit). The tests run offline using recorded responses, temporary SQLite databases and a local assessment service.

## Unit tests

From `src/PnP.Scanning`:

```powershell
dotnet test PnP.Scanning.Core.Tests/PnP.Scanning.Core.Tests.csproj
```

For the classic pages assessment, the unit tests cover the pure sub-logic (wiki/HTML parsing, web part mapping, layout detection, home-page detection, usage-row parsing, the storage and CSV-export cores, etc.).

Collection and analysis tests also cover command line argument binding, the local service, saved snapshots, repeated analysis, pause and restart, and report export. These tests run as part of the same `dotnet test` command and the existing build/test workflow.

## End-to-end validation

The SharePoint CSOM code paths that cannot be unit-tested in isolation — reading a page's web parts via the `LimitedWebPartManager` and reading `Web.CanModernizeHomepage` — are validated by running an actual assessment against a test tenant with the CLI:

```powershell
microsoft365-assessment.exe start --mode ClassicPage `
  --authmode application --tenant contoso.sharepoint.com `
  --applicationid <app-id> --certfile <path-to-cert.pfx> `
  --siteslist "https://contoso.sharepoint.com/sites/team"

microsoft365-assessment.exe report --id <assessment-id> --mode CsvOnly --path "c:\reports"
```

Inspect the exported `classicpages.csv`, `classicpagewebparts.csv`, `classicwebpartunique.csv` and the web/site summaries to confirm the assessment produced the expected data. See [Run a classic pages assessment](../classic/assess.md) for the full set of options.
