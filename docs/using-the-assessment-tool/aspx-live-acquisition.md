# Lossless ASPX discovery and live acquisition

The `aspx-inventory` and `aspx-acquisition` commands are advanced, non-default entry points for
building an auditable physical ASPX denominator. They do not replace the existing Classic scan.

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

All acquisition traffic uses:

```text
User-Agent: testtraffic-smr
```

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
