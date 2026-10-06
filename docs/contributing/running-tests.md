# Running the tests

The engine (`PnP.Scanning`) has a unit test project, `PnP.Scanning.Core.Tests` (xUnit). The tests are pure unit tests that run offline with no external dependencies.

## Unit tests

From `src/PnP.Scanning`:

```powershell
dotnet test PnP.Scanning.Core.Tests/PnP.Scanning.Core.Tests.csproj
```

For the classic pages assessment, the unit tests cover the pure sub-logic (wiki/HTML parsing, web part mapping, layout detection, home-page detection, usage-row parsing, the storage and CSV-export cores, etc.).

## Physical Page source integration and full Release validation

The [Linux offline test recipe](page-source-offline-tests.md) prepares a local
net8.0 runtime host while retaining the supplied SDK, maps the managed read-only
dependency and redirects every build/cache/documentation output. It gives the
full solution Release commands and the independent `Category=PageInherits`
command. The category starts from synthetic discovery/acquisition inputs and
checks the real adapter/parser, native write/reopen/retrieval, independent CSV
and backup/restore/downgrade. Existing family/routing/accounting tests remain in
the full suite. See [physical Page source evidence](../classic/page-source-evidence.md)
for interpretation and limitations.

## End-to-end validation

This section describes a possible **separately authorized** live readback, not
work performed by the offline fixtures or permission to run tenant/server jobs.
Offline results make no live/full-tenant claim. Any live claim needs bounded
authorization and readback; if the original controlled test cohort is used,
retain its `User-Agent: testtraffic-smr`.

The SharePoint CSOM code paths that cannot be unit-tested in isolation — reading a page's web parts via the `LimitedWebPartManager`, reading `Web.CanModernizeHomepage`, and the page-usage search query — are validated by running an actual assessment against a test tenant with the CLI:

```powershell
microsoft365-assessment.exe start --mode Classic --classicinclude Pages `
  --authmode application --tenant contoso.sharepoint.com `
  --applicationid <app-id> --certfile <path-to-cert.pfx> `
  --siteslist "https://contoso.sharepoint.com/sites/team"

microsoft365-assessment.exe report --id <assessment-id> --mode CsvOnly --path "c:\reports"
```

Inspect the exported `classicpages.csv`, `classicpagewebparts.csv`, `classicwebpartunique.csv` and the web/site summaries to confirm the assessment produced the expected data. See [Run a classic pages assessment](../classic/assess.md) for the full set of options.
