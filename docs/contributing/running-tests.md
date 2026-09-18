# Running the tests

The engine (`PnP.Scanning`) has an xUnit project, `PnP.Scanning.Core.Tests`, containing unit tests and offline integration tests. Test execution needs no tenant, credentials or running assessment daemon. Building/restoring still requires the repository's normal SDK and source/package dependencies.

## Unit tests

From `src/PnP.Scanning`:

```powershell
dotnet test PnP.Scanning.Core.Tests/PnP.Scanning.Core.Tests.csproj
```

For the classic pages assessment, the unit tests cover the pure sub-logic (wiki/HTML parsing, web part mapping, layout detection, home-page detection, usage-row parsing, the storage and CSV-export cores, etc.).

## Native discovery regression entry point

From the repository root, run the native discovery/metadata/database/report checks directly through .NET:

```powershell
dotnet test src/PnP.Scanning/PnP.Scanning.Core.Tests/PnP.Scanning.Core.Tests.csproj `
  --configuration Release `
  --filter "Category=NativeScanIntegration" `
  --logger "trx;LogFileName=native-scan-integration.trx"
```

Use a separate `--artifacts-path <directory>` if a scanner is currently running from the usual build output. Do not overwrite that process's binaries. This entry point does not start, stop or connect to the assessment daemon and cannot reuse a stale server on port 25010.

The suite exercises production `AssessmentWebDiscovery`, `AssessmentDiscoveryWriter`, `PageScanComponent.LoadPhysicalPageAsync`, the real EF migrations and `ReportManager.ExportClassicReportDataAsync`. In particular it checks:

- Partial/denied enumeration and missing expected children remain Scope rows; other discovered files survive.
- Repeated Web discovery does not duplicate physical pages; cancellation retains committed rows and concurrent Web writers use separate EF contexts.
- Physical-page metadata reuses the native `LoadListDataAsStreamAsync` reader with explicit CAML ViewFields and an exact discovered item-ID filter. Even REST `$select=*` (`IListItem.All`) omits computed fields such as `FileRef`, `HTML_x0020_File_x0020_Type` and `ClientSideApplicationId`. Optional modern-only ViewFields do not cause classic libraries to fail as an explicit REST selection would.
- Different libraries can each contain item `1` without collapsing their physical page URLs or colliding in the database.
- Publishing/Enterprise Wiki, Wiki, Web Part, custom ASPX and Modern metadata are classified correctly in the replay fixtures.
- A wrong list, missing/wrong item or mismatched `FileRef` is rejected rather than assessing another file.
- The metadata query is bounded to the discovered item, clears previously cached list items and omits `Editor` when user information is disabled.
- Native `discovery.csv` preserves modern pages, nullable home-page state, identities and enrichment errors; `classicpages.csv` retains Classic-only assessment rows. Quotes, commas and newlines round-trip through CSV; no native gap CSV is produced.

`NativePageMetadataReplayTests` replaces only the PnP SDK list/item boundary with a strict offline test double. It uses synthetic metadata shaped after the cross-library item-ID failure, not captured tenant responses. It does **not** validate PnP SDK HTTP serialization, authentication, actual SharePoint enumeration completeness, CSOM Web Part extraction, TPL scheduling or the live restart lifecycle. Passing this suite is a local regression gate, not tenant acceptance.

## End-to-end validation

The live SDK/CSOM code paths — reading a page's web parts via the `LimitedWebPartManager`, reading `Web.CanModernizeHomepage`, and discovering actual files/scopes — are validated by running an actual assessment against a test tenant with the CLI:

```powershell
microsoft365-assessment.exe start --mode Classic --classicinclude Pages `
  --authmode application --tenant contoso.sharepoint.com `
  --applicationid <app-id> --tenantid <tenant-id> --certpath 'My|CurrentUser|<thumbprint>' `
  --skipusageinformation `
  --siteslist "https://contoso.sharepoint.com/sites/team"

microsoft365-assessment.exe report --id <assessment-id> --mode CsvOnly --path "c:\reports"
```

Inspect the exported `discovery.csv`, `classicpages.csv`, `classicpagewebparts.csv`, `classicwebpartunique.csv` and the web/site summaries to confirm the assessment produced the expected data. A finished scan is not necessarily complete: inspect failed/partial Scope rows, page assessment failures and the Web/post-scan status. Compare scans with the same declared scope, authentication and relevant content snapshot; a raw count from a different identity or date is not an equivalence proof. See [Run a classic pages assessment](../classic/assess.md) for the full set of options.
