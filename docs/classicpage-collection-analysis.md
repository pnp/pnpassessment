# Classic Pages collection and offline analysis

PR #168 includes both the [H-F foundation](collection-analysis-foundation.md), originally introduced at `823e852`, and this production `classicpage` integration. It splits the existing Classic page implementation and leaves the other scanners on their existing paths. It does not add ASPX file-body downloads, Handler analysis, or the publishing-layout rules from #166/#167.

## Commands

```text
microsoft365-assessment start --mode classicpage --tenant contoso.sharepoint.com --applicationid <app-id> --siteslist https://contoso.sharepoint.com/sites/a --authmode application --certfile <certificate.pfx> --threads 4
microsoft365-assessment collect --mode classicpage --tenant contoso.sharepoint.com --applicationid <app-id> --siteslist https://contoso.sharepoint.com/sites/a --authmode application --certfile <certificate.pfx> --threads 4
microsoft365-assessment analyze --id <assessment-id> --snapshot-id <snapshot-id>
microsoft365-assessment report --id <assessment-id> --analysis-run-id <analysis-run-id> --mode CsvOnly --open false
```

`start --mode classicpage` invokes the separate `StartPipeline` RPC and runs collection, sealing, analysis, and report finalization. `collect` stops after sealing. Each `analyze` creates a new run. `analyze` accepts no authentication or tenant options. `--module` is optional for this mode; a conflicting module is rejected.

The page flags are `--homepageonly`, `--skipusageinformation`, `--skipuserinformation`, `--exportwebpartproperties`, and `--auditlogwindowdays`. `--classicinclude` may only select `Pages` in the new mode. A tenant and application ID are required. Interactive authentication runs in the collection host; device-code instructions are emitted in that host's log. Application authentication uses the existing certificate options and protected persisted configuration.

`status`, `list`, `pause`, `restart`, and `stop --id` use H-F scheduling. Records include acquisition receipts, not just physical pages, and are not placed in the legacy site-count fields. Restart retains the snapshot, run, rules, parameters and committed observations/results. After sealing, restart does not initialize collection authentication. A missing implementation or changed mapping resource rejects restart.

The old `start --mode classic` remains a mixed-component legacy scan. Its page adapter shares the extracted readers and pure rules, but retains legacy storage/lifecycle. InfoPath, Workflow, Lists, Extensibility, AddInsACS, Alerts and other scanner classes are not migrated.

## Refactoring inventory

| Original object/responsibility | Collection | Offline analysis / compatibility |
| --- | --- | --- |
| `ClassicScanner` page initialization, execute and post-scan portions | `ClassicPageOnlineSource`, `ClassicPageCollectionModule` | `ClassicPageAnalysisModule`, report finalizer; legacy mixed scan keeps a page adapter |
| Site and Web enumeration | Fixed site/Web/template receipts; optional cancellation/context-key parameters on the shared service | Scope validation, Web/site summaries; original defaults retained for old callers |
| `ClassicPageDiscoveryComponent`, live discovery provider | Reused discovery provider; durable REST/modeled response journal; final Web inventory | Coverage and assessment disposition are independent projections |
| `AssessmentWebDiscovery` / `AssessmentDiscoveryWriter` | Existing discovery walk through a source sink; no writes to legacy report tables in the new mode | Shared pure reconciliation/coverage rules; legacy DB writer retained |
| `LoadPhysicalPageAsync`, list queries and raw field reads | Typed detached list-item fields, IDs, URLs, dates, user/URL values, Wiki and classification fields | `ClassicPageRules`, `ClassicPageProjection`; no SDK list items |
| `GetPageType`, publishing/modern admission, CP codes | Only routing necessary online reads | Existing classification order, Modern exclusion, remediation and page-report admission |
| Blog Posts list | Original fields and paged batches, including empty/missing-list facts | Blog rows, CP4 and home-page selection |
| `PageWebPartExtractor` | `PageWebPartSourceReader`: shared manager/ControlId reads, IDs, zones, positions, flags, properties and permitted XML exports | `PageWebPartAnalysis`: XML/property type, inventory ordering and rows; old extractor is a facade |
| `WikiContentParser` | ControlId location needed to acquire Web Parts | Shared pure text/media/placeholder parsing and ordering |
| Layout detection | `vti_setuppath`, Wiki HTML, PublishingPageLayout URL/description | Existing Web Part/Wiki/Publishing layout rules |
| Welcome page / CanModernizeHomepage / fallback reads | Values and individual read states; features, template, master, language, localized resource and display form | Existing API-first and fallback home-page decisions; unavailable and successful-empty values differ |
| Mapping and readiness | No mapping verdicts | Shared mapping model plus pure `SnapshotPageMappingCalculator`; mapping digest fixed at enqueue |
| Audit Graph query, polling and pagination | `ClassicPageAuditCollector`; original window, filters, query IDs, HTTP responses, chunk states and record bodies | `ClassicPageAuditAnalysis`; counts and bounded case-insensitive distinct-user reduction, partial/error/skip coverage |
| Page/Web/unique-Web-Part/site/publishing rollups | No analysis-table writes | Run-scoped finalization and detached report rows |
| CSV/PBIT export | No raw-input synthesis from existing reports | `ClassicPageReportExporter`; existing model/column mappings and Classic template |
| Old page/site storage entrypoints | Retained for legacy only | New mode writes `AnalysisResults` and `ClassicPageReportRows`, without overwriting old tables |

Publishing master-page rollup columns follow the old **Pages-only** baseline: Extensibility is not run, and its results are not borrowed. Page layouts, modification dates, publishing page counts and Web counts are calculated from the selected page snapshot.

## Ownership and schema

Input version is `classicpage-source-v1`; current rule version is `classicpage-v1`. Source DTOs preserve field type, absent versus explicit-null fields, raw text/XML, GUIDs and individual acquisition states. Their BLOBs and metadata use versioned JSON. ASPX physical discovery does not imply its file-body bytes were downloaded.

H-F's six source/phase/result tables are retained. Migration `ClassicPageRunReports` only adds `ClassicPageReportRows`, keyed by analysis run, report kind and row key. Triggers prevent updates/deletes of published rows and require a running analysis for publication. Old tables, extra historical SQL columns and migration history remain intact.

Analysis uses contract DTOs rather than EF entities. It receives only snapshot read, result read/write and report-write capabilities. The dependency audit includes the reused pure Wiki/layout/home-page/mapping helpers and their IL. SDK, HTTP, authentication, data protection, storage services and collection services are forbidden in the analysis graph.

Completed discovery requests, audit submissions/polls/pages, fixed site/Web enumeration and terminal page captures are committed before advancing. Resume reconstructs discovery using original request receipts, and resumes the same audit query and continuation. An interrupted atomic page capture may be retried; already committed page captures are never replaced. Acquisition cancellation or infrastructure/commit failure does not seal or start analysis. Settled per-record failures remain reportable.

Sealing and reading verify persisted length/hash, source ownership, fixed membership and page-module input references. Blob validation streams artifacts; collection keeps receipt headers rather than all response bodies, and analysis preparation indexes identities/shared metadata rather than all page XML and audit bodies. Derived report rows and audit aggregates still require memory proportional to their result count.

Finalization publishes all report rows transactionally and records their digest. It can resume after publication without rewriting rows/results. Export checks the selected source manifest and report digest. Default report selection chooses the latest successfully completed analysis; an explicitly selected unfinished/unknown run fails. Default folders include the run ID. CSV column contracts and the Classic PBIT are reused; a sidecar `analysis-run.json` identifies the snapshot/rule/run. Skipping usage produces no audit CSV.

No compression or cross-snapshot content deduplication is introduced. Reanalysis shares the source BLOBs and adds results/projections. Storage receipts measure page artifacts, shared discovery/audit inputs and per-run outputs separately; small recorded fixtures are not tenant-wide size estimates.

## Verification

Run `build/verify-classicpage.ps1 -Suite Release` with a sibling PnP Core checkout at tag `1.18.0`, a .NET 8 SDK selected in the workflow parent, and local EF 8.0.3 tools. It produces TRX, dependency/schema/source metadata and SQLite receipts without changing global tools.

`CP01`–`CP15` cover native start/collect/analyze/report, independent reanalysis, pause/reopen/restart, acquisition failures, Blog/home/usage options, preflight, immutable/corrupt evidence, input references, finalizer recovery, real Graph acquisition through a test HTTP handler, actual discovery-journal response serialization/replay, CSOM transport cancellation, list-stream identity when the SDK omits ID from Values, and requested SDK folder/file capture without remote projection. H-F `T28`–`T33` and the existing Release tests remain required.

The native tests execute the production page collector/analyzer with recorded online sources and loopback gRPC. HTTP transport tests execute production audit/discovery components with local handlers. They do not establish successful collection against a live tenant or visually validate a Power BI Desktop report. Those outcomes must be recorded separately.
