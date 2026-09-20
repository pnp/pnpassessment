# Lossless ASPX discovery and live acquisition

Full ASPX discovery is part of the existing Classic Pages scan. Start one native assessment,
then generate its normal report using the same ScanId. There is no additional authentication,
registry file, manifest, acquisition run or output-path configuration for this workflow.

```powershell
microsoft365-assessment start `
  --mode Classic --classicinclude Pages `
  --tenant contoso.sharepoint.com `
  --applicationid <application-id> --tenantid <tenant-id> `
  --authmode Application --certpath 'My|CurrentUser|<thumbprint>' `
  --threads 4 --skipusageinformation

microsoft365-assessment report --id <scan-id> --mode CsvOnly --open false
```

Use `--siteslist`/`--sitesfile` for a declared subset; omit them for tenant enumeration.
Application authentication uses the native certificate support and requires the read-only
application permissions Microsoft Graph `Sites.Read.All` and SharePoint `Sites.Read.All`.
Audit usage has its own permissions and is skipped in the example.

## One native discovery report

The normal report contains `discovery.csv`, exported from the scan's `assessment.db`:

- `RowType=Page`: a discovered physical ASPX, deduplicated by owning Site/Web/file identity.
- `RowType=Scope`: an attempted tenant, site, Web, list, folder or API surface, including
  failures and expected children that were not observed. These rows are not page counts.
- `DiscoveryStatus`: `Discovered` for known pages, or the scope's `Pending`, `Complete`,
  `Empty`, `Partial`, `Denied`, `Failed`, `Cancelled`, `Unknown` or `PolicyExcluded` state.
- `AssessmentStatus`: separate page-enrichment outcome. A metadata/Web Part failure never
  removes a discovered page or changes its existence to false.
- `ErrorStage`, `ErrorCodes`, `ErrorDetail`: unresolved discovery/enrichment evidence in the
  same CSV. Unknown child counts and unavailable identities remain null, not zero or guessed.
- `HomePage` is nullable. Modern and non-transformable physical ASPX remain in discovery even
  when they do not become Classic page-assessment rows.

There is no separate gap CSV for the native scan. Existing `classicpages.csv`, Web Part and
summary CSVs remain available; the page detail also gains identity and discovery/assessment
status fields. Do not count Scope rows as pages or assume that a finished job proves complete
coverage. The existing Power BI visuals are not a discovery-completeness dashboard; use the
unified CSV for the new coverage fields.

The native Site/Web TPL queues schedule Web-local discovery before classification and enrichment.
Hidden libraries, catalogs, page-family classification and publishing features are not discovery
admission gates. `--homepageonly` limits assessment, not the physical ASPX denominator. Existing
non-file Blog post assessment remains supported separately from physical ASPX inventory.

Each Web owns its provider/context state; short discovery database writes are serialized. Batch
results survive a later denied/failed scope. The native restart lifecycle remains Web-granular:
restarted Webs replay discovery idempotently, rather than promising continuation directly from an
individual saved folder token. Run history/diagnostic replay is not a second user-facing scan.

## Standalone diagnostic commands

The `aspx-inventory` and `aspx-acquisition` commands remain available for offline contract replay
and independent diagnostic comparisons. Their sealed multi-volume outputs below are not inputs
required by the native Classic scan or its report.

## Contract boundaries

The physical volume uses:

- discovery contract `classic-page-discovery/v3`;
- SQLite schema `classic-page-discovery-sqlite/v3`;
- JSON output `classic-page-discovery-output/v3`.

The live command also writes the current reference, aggregate and terminal-receipt companion
volumes. A generated platform registry is intentionally a separate deliverable; a compatible
minimal registry can be used when validating physical discovery, in which case the aggregate
verdict can remain `Unknown` without invalidating the physical volume.

## What is preserved

Each discovered physical ASPX row can retain:

- Site collection, Web, list, folder, list-item and file identities;
- physical server-relative locator and file name;
- nullable home-page status;
- content type and derived page type when list-item metadata is available;
- hidden-library and customized-page status;
- observation method, permission context and source evidence.

The canonical key is based on the owning Site/Web and physical file identity. List and folder
provenance do not split one file when folder enumeration, Forms and Views observe the same object.

## Failure semantics

`Denied`, `Failed`, `Truncated` and `Unknown` are retained. They are never converted to `Empty` or
`Complete`. The JSON output includes detailed scope and gap rows, and every physical JSON output
also produces an RFC 4180-compatible `<physical-output>.gaps.csv` sidecar with:

```text
ScopeKey,SourceKind,GapCode,Detail,Resolved
```

If a PnP Core modeled Web-root folder request fails, acquisition retains that evidence and attempts
the equivalent authenticated REST surface. A failed fallback remains an explicit denied/failed gap.

## Authority modes

`declared_subset` resolves stable Site and root-Web identities for every supplied `--site` and then
uses the PnP Core Web authority to enumerate subwebs. It never claims tenant-wide visibility.

`product_tenant_authority` obtains the site-collection denominator from PnP Core Admin and can claim
tenant visibility only when site and Web authority collections finish without continuation,
exclusion or failure.

The direct SharePoint REST collection requests issued by the live acquisition provider use:

```text
User-Agent: testtraffic-smr
```

The scanner's shared PnP Core configuration keeps its normal product user agent, so unrelated
assessment modes and page-enrichment requests are not relabeled as acquisition test traffic.

## Offline replay

```powershell
microsoft365-assessment aspx-inventory `
  --input sealed-input.json `
  --manifest discovery-manifest.json `
  --database physical.sqlite `
  --output physical.json
```

Offline replay validates the input hash, contract/schema versions and resume compatibility before
mutating the ledger. Duplicate batches are no-ops; conflicting replays fail closed.

## Live declared-subset example

```powershell
microsoft365-assessment aspx-acquisition `
  --site https://contoso.sharepoint.com/sites/classic `
  --scope-mode declared_subset `
  --tenant contoso.sharepoint.com `
  --authMode Device `
  --applicationId <public-client-application-id> `
  --tenantId <tenant-id> `
  --manifest discovery-manifest.json `
  --registry minimal-registry.json `
  --physical-database physical.sqlite `
  --physical-output physical.json `
  --reference-database reference.sqlite `
  --reference-output reference.json `
  --aggregate-output aggregate.json `
  --terminal-receipt terminal.json `
  --platform-build <observed-platform-build> `
  --snapshot-fence <immutable-as-of-label> `
  --permission-context <non-secret-identity-label> `
  --visibility-boundary <authorized-scope-label>
```

The application must be configured as a public client for Device/Interactive authentication. The
minimum delegated permissions for declared-subset read validation are Microsoft Graph
`Sites.Read.All` and `User.Read`, plus SharePoint `AllSites.Read`.

## Resume compatibility

Product commit, SDK commit, binary, dependency, build and environment provenance are recorded but do
not by themselves invalidate a resume. Resume is accepted across equivalent builds only when the
contract, schema, input, scope policy, tenant authority, snapshot and registry semantics remain
compatible. Semantic drift is rejected before enumeration or ledger mutation.

## Acceptance

A change is acceptable when:

- offline first/resume and replay tests pass;
- site/root-Web/subweb authority relationships are preserved;
- BaseType `-1..5` applicability tests pass;
- physical identities round-trip through SQLite and JSON;
- repeated equivalent observations reconcile without duplicate-key failures;
- conflicting observations remain explicit gaps;
- a live declared-subset run writes physical, reference, aggregate, gap CSV and terminal volumes;
- terminal receipt state and exit code agree with the completed run.
