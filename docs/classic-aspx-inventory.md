# ASPX inventory and display-form declarations

Classic **Pages** inventories physical ASPX resources and records nonempty `List.DefaultDisplayFormUrl` and registered `ContentType.DisplayFormUrl` declarations. It does not enumerate list items to infer item-only pages, per-item renderer bindings or their counts. A form declaration is a possible rendering route, not proof that any item uses it. This acquisition evidence is separate from the classic-page modernization assessment.

For a full Pages scan, each scheduled Web discovers files below its Web root and every observed `List.RootFolder`, recursively. This includes ordinary lists, document libraries, hidden lists and catalogs when the scanning identity can enumerate them. The discovery worker records physical `.aspx` files and metadata references; it does not enumerate list items. After **all selected Webs** have finished discovery, the scan-wide post-scan phase reads the persisted ASPX observations, acquires file content and writes the native inventory. The classic-page modernization assessment still runs in each Web worker and remains a separate result.

Folder traversal lists file metadata for each folder and admits `.aspx` paths locally; it does not have a server-side `*.aspx` filter. Large folder trees therefore cost requests and metadata transfer in proportion to the folders and files observed. Unreadable folders, excluded Webs and virtual or item-only rendering routes remain outside a proven physical-file inventory; their absence cannot be inferred from a finished scan.

Use the existing [Classic assessment](classic/assess.md) and [authentication setup](using-the-assessment-tool/setupauth.md). Examples show page options; supply your environment's authentication arguments. Placeholders are not deployment targets.

## Start and report

~~~text
microsoft365-assessment.exe start --mode Classic --classicinclude Pages --tenant <tenant-host> --siteslist "<site-url>,<second-site-or-web-url>"
microsoft365-assessment.exe start --mode Classic --classicinclude Pages --tenant <tenant-host> --siteslist "<site-url>" --homepageonly
microsoft365-assessment.exe report --id <assessment-id> --mode CsvOnly --delimiter Comma --path <report-directory> --open false
~~~

`--siteslist` and `--sitesfile` select declared roots. A supplied subweb remains a scheduled root and its descendant subtree; it is not automatically replaced by its parent site collection. Omitting both selectors uses tenant enumeration, a different scope dependent on authority and visible sites. Enumeration failure or a known-root fallback is not a complete tenant inventory.

`--homepageonly` targets each scheduled web's welcome page without enumerating every list, folder and form surface. No Pages scan enumerates all list items. `HomePageOnly` and `DeclaredFullInventory` describe the requested page scope, not a tenant-coverage verdict. A finished assessment or successful report command alone does not establish complete acquisition.

### Web-template exclusions

Use `--excludesitetemplates` with the Pages component:

| Value | Meaning |
|---|---|
| Omitted | Default patterns `SPSPERS#*,APP#*`. |
| `"BLOG#*"` | Replace the defaults, not add to them. |
| `"SPSPERS#*,APP#*,BLOG#*"` | Explicitly retain the defaults and add a pattern. |
| `none` | Clear the exclusions; it must be the sole value. |

Patterns are comma-separated, case-insensitive web-template matches supporting `*` and `?`. Empty values/entries and mixing `none` with other patterns are invalid. The policy is frozen with the assessment and evaluated per observed **web**. An excluded parent is not subtree pruning: each child has its own template decision. An unavailable template is unknown, not proof of admission/exclusion. Clearing a filter does not grant content access.

## Optional versioned platform catalog

`--platformaspxcatalog <catalog-file>` supplies an explicit UTF-8 JSON catalog of platform ASPX reference identities. Its **exact input bytes**, source and version are frozen with the assessment; the SHA256 digest changes even when only serialization changes. Restart uses retained evidence rather than treating a mutable path as a fresh deployment inventory.

Required root fields are `schemaVersion`, `catalogVersion`, `source` and `entries`. Each entry requires `id`, `deploymentIdentity`, `culture` and a query-independent ASPX `path`; `aliases` is optional. This synthetic example declares no real deployment endpoint or Handler:

~~~json
{
  "schemaVersion": "platform-aspx-catalog/v1",
  "catalogVersion": "example-v1",
  "source": "synthetic-documentation-example",
  "entries": [
    {
      "id": "example-renderer",
      "deploymentIdentity": "synthetic-deployment",
      "culture": "neutral",
      "path": "/_layouts/15/example-renderer.aspx",
      "aliases": [],
      "declaredInherits": null,
      "handlerEvidenceSource": null
    }
  ]
}
~~~

Paths start with `/_layouts/`, end with `.aspx`, and contain no query, fragment, backslash, traversal segment or encoded path separator. Unversioned `/_layouts/...` is catalog hive14; `/_layouts/15/...` is hive16. They are separate identities. Explicit numeric `/14/` and `/16/` namespaces are unsupported: hive14 means the unversioned URL namespace. Do not invent `/_layouts/16/...` aliases or collapse unversioned and `/15/` members. Aliases stay within one hive. Deployment identity, culture, version and entry identity are significant; ambiguous paths do not justify guessing a member.

A non-null `declaredInherits` requires a nonempty `handlerEvidenceSource` and is a **versioned catalog reference**, not proof of a tenant file or executing Handler. Catalog payload bytes are not exported as Handler proof; use `CatalogSource`, `CatalogVersion`, `CatalogDigest` and `CatalogEntryKey`. A missing catalog leaves platform membership unknown, not evidence that no platform ASPX exists. A catalog resource is seeded once per assessment, not once per web, and can have zero observed bindings.

## Identity and observed counts

| Fact | Interpretation |
|---|---|
| Tenant resource | `AspxKey` uses real `SiteCollectionId` + `FileUniqueId`. Paths/URLs/observing webs are metadata, not extra identities. Unresolved paths do not acquire invented file GUIDs. |
| Platform resource | A frozen catalog deployment/entry identity. Tenant file GUIDs may be null; observing another web does not multiply the resource. |
| List display-form declaration | One nonempty `DefaultDisplayFormUrl` observed in a list metadata row; the reference retains its `ListId`. |
| Registered content-type display-form declaration | One nonempty `DisplayFormUrl` observed on a content type registered with a list; the reference retains `ListId` and `ContentTypeId`. |
| Item binding in older scans | Historical rows retain `(ScanId, AspxKey, SiteCollectionId, WebId, ListId, positive ItemId)` identity. New scans do not acquire these rows. |

The declaration counts measure metadata rows with nonempty form URLs. They do not measure unique forms, actual renderings, item-only pages or list item counts. Physical ASPX files can still enter inventory through file discovery, including a display-form file when it is observed as a file.

`DistinctItemBindingCount` remains the SQL count of stored binding pairs for compatibility with saved scans. A zero in a new scan means item bindings were **not collected**. Check `itemBindingCollectionState` in `aspxsummary.json` before interpreting it. Full Pages scans with this policy receive the `CompleteDeclaredSubset` coverage verdict, since item-only pages are outside the collected subset.

Service-side classic rendering telemetry could measure render events or distinct item IDs seen during a time window. It would measure observed usage, not the number of items that could render but were never requested.

### Handler source facts, not classification

`HandlerDeclaredType` retains the source declaration, including an explicit empty declaration. Interpret normalized/effective values with `FileReadState`, `HandlerReadState`, `HandlerParseState`, `HandlerSource`, `HandlerDefaultProvenance`, `HandlerReasonCode` and error/provenance fields. An effective type can remain null.

Only complete, available **actual file bytes** support source parsing. Denied, unavailable, truncated or invalidly encoded bytes, rendered HTML and path-only observations do not prove an actual Handler. A readable source without `Inherits` may carry the explicitly labelled `FrameworkDefaultAssumptionNotConfigResolved`; this is not resolved `web.config` or a runtime type check. Catalog declarations stay versioned references; types/assemblies are not loaded to verify execution.

Acquisition does not use a later classic/modern, user/non-user or modernization classification as an admission gate. Existing page assessment results remain separate from physical resource existence and form declarations.

## SQL and native CSVs

SQL tables remain `ClassicAspxInventories` and `ClassicAspxItemBindings` for saved-scan compatibility. Native Classic reports add:

| Output | Key fields |
|---|---|
| `aspxinventory.csv` | Physical ASPX and catalog identities, file and Handler evidence, coverage, provenance and `DistinctItemBindingCount`. A zero binding count in a new scan means bindings were not collected. |
| `aspxitembindings.csv` | Compatibility output for saved binding observations. New scans leave it header-only and mark `ItemBindings=NotCollected` in the Info file. |
| `classicaspxinfo.txt` | Acquisition marker/scope, explicit item-page and binding collection state, and nullable-text encoding. Missing native marker + empty tables means `NotAcquired/Legacy`; missing marker + observed rows means `ObservedWithoutNativeMarker` / `UnknownContract`. |

The canonical filenames are `aspxinventory.csv` and `aspxitembindings.csv`. The earlier names `classicaspxinventories.csv` and `classicaspxitembindings.csv` remain byte-identical compatibility aliases. Nullable text in these four files uses the same BackslashNullV1 encoding.

`aspxsummary.json` uses `schemaVersion: "aspx-inventory-summary/v1"`. It reports the assessment ID, projection contract, requested scope, frozen template exclusions, catalog source/version/digest, observed inventory and binding-stream counts, inventory coverage states, and canonical-file lengths and SHA256 hashes. For new scans, `itemOnlyPageCollectionState` and `itemBindingCollectionState` are `NotCollected`. `displayFormDeclarations.listDefaultReferenceCount` and `contentTypeReferenceCount` count observed metadata references, not item counts. Legacy scans without the new policy marker report the collection state as `Unknown`. The summary makes no tenant-completeness assertion.

Rows stream in deterministic key order; empty tables produce typed header-only CSVs. `Unknown`, `Incomplete` and `ChangedDuringScan` must not become complete tenant coverage. Retain the assessment ID, source/catalog version and declared scope when comparing results. Homepage and full-inventory totals are not equivalent universes.

### BackslashNullV1 nullable text

Only nullable **text** in the two new ASPX inventory CSVs uses `BackslashNullV1`, declared in Info. Parse quoted CSV first, then decode:

| Original nullable text | Parsed CSV field before nullable-text decoding |
|---|---|
| NULL | `\N` |
| Explicit empty string | Empty field, unchanged |
| Literal `\N` | `\\N` |
| Text beginning `\name` | `\\name` |

Exact single-backslash `\N` decodes to NULL. Otherwise, a leading pair of backslashes loses exactly one backslash; all other content, including empty text, stays unchanged. Encoding prepends one backslash to a non-null string already starting with a backslash. Thus a literal `\N` cannot collide with NULL, and a bad explicit `Inherits=""` is not erased.

The typed `ClassicAspxInventoryExport` and `ClassicAspxItemBindingExport` DTOs apply this codec. External consumers must reverse it after CSV parsing. Do not globally equate empty cells with NULL or change decoding for existing CSVs. Nullable GUID/numeric values keep their normal typed CSV representation.

## Compatibility and retained observations

Existing Classic CSVs and Power BI templates keep their contracts; the normalized files are additional outputs. `classicpages.csv` is still selected classic-page assessment data, not a replacement for the ASPX inventory. Upgrades create schema without inventing historical inventory, bindings or Handler facts. Header-only legacy inventory files do not mean there was no ASPX.

Pause/restart retain assessment evidence and merge repeated observations by stable resource keys. Saved item-binding rows remain available for compatibility; the new acquisition path does not add item bindings. First/latest timestamps and provenance preserve context. Location or Handler changes can report `ChangedDuringScan` rather than proving one immutable state. Metadata-only or denied observations do not erase already acquired source facts. These semantics do not promise service-side exactly-once execution, a transactional tenant snapshot or coverage of unreadable content.

## Saved-scan compatibility

New scans declare `AspxProjectionContract=aspx-inventory/v1` and `AspxScopeMode`. Saved preview declarations are recognized and normalized during export without backfilling properties or changing facts. When both property families exist, the current family is authoritative; unknown or incomplete current declarations never borrow an older version or scope. Retained raw evidence keys and pagination receipts are preserved during resume, and changed evidence remains uncertain rather than gaining complete coverage.
