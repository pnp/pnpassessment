# ASPX inventory and item bindings

Classic **Pages** records physical ASPX resources separately from the real list items that use them. This acquisition evidence is not the existing classic-page modernization assessment. A resource needs no backing item to enter inventory; a non-ASPX document can use an ASPX renderer without becoming an ASPX resource itself.

Use the existing [Classic assessment](classic/assess.md) and [authentication setup](using-the-assessment-tool/setupauth.md). Examples show page options; supply your environment's authentication arguments. Placeholders are not deployment targets.

## Start and report

~~~text
microsoft365-assessment.exe start --mode Classic --classicinclude Pages --tenant <tenant-host> --siteslist "<site-url>,<second-site-or-web-url>"
microsoft365-assessment.exe start --mode Classic --classicinclude Pages --tenant <tenant-host> --siteslist "<site-url>" --homepageonly
microsoft365-assessment.exe report --id <assessment-id> --mode CsvOnly --delimiter Comma --path <report-directory> --open false
~~~

`--siteslist` and `--sitesfile` select declared roots. A supplied subweb remains a scheduled root and its descendant subtree; it is not automatically replaced by its parent site collection. Omitting both selectors uses tenant enumeration, a different scope dependent on authority and visible sites. Enumeration failure or a known-root fallback is not a complete tenant inventory.

`--homepageonly` targets each scheduled web's welcome page without enumerating every list, folder, form and item surface. `HomePageOnly` and `DeclaredFullInventory` describe the requested page scope, not a tenant-coverage verdict. A finished assessment or successful report command alone does not establish complete acquisition.

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
| Item binding | `(ScanId, AspxKey, SiteCollectionId, WebId, ListId, positive ItemId)`. `ItemGuid` is optional supplementary evidence, not the key or an admission requirement. |
| Repeated sources | `BindingKinds` flags and `BindingProvenanceJson` combine reasons on one item/resource pair, not one row per reason. |

One renderer used by 100 distinct scoped items gives **one inventory row and 100 bindings**. The same pair found through a list form and content type gives **one binding with both reasons**. An ASPX without a backing item gives **one inventory row and zero bindings**, not an invented item. One item can bind to several ASPX resources. Different site/web/list GUIDs remain distinct even if integer item IDs match.

`DistinctItemBindingCount` is the SQL count of unique normalized binding pairs for that assessment/resource—not source flags, nullable `ItemGuid` values, mixed discovery rows, visits or platform-wide usage. Inventory is left-joined to the count, retaining zero-binding resources. Counts are **observations in the declared scope**; zero does not establish complete absence when coverage is unknown, incomplete or changed.

### Handler source facts, not classification

`HandlerDeclaredType` retains the source declaration, including an explicit empty declaration. Interpret normalized/effective values with `FileReadState`, `HandlerReadState`, `HandlerParseState`, `HandlerSource`, `HandlerDefaultProvenance`, `HandlerReasonCode` and error/provenance fields. An effective type can remain null.

Only complete, available **actual file bytes** support source parsing. Denied, unavailable, truncated or invalidly encoded bytes, rendered HTML and path-only observations do not prove an actual Handler. A readable source without `Inherits` may carry the explicitly labelled `FrameworkDefaultAssumptionNotConfigResolved`; this is not resolved `web.config` or a runtime type check. Catalog declarations stay versioned references; types/assemblies are not loaded to verify execution.

Acquisition does not use a later classic/modern, user/non-user or modernization classification as an admission gate. Existing page assessment results remain separate from resource existence and normalized bindings.

## SQL and native CSVs

SQL tables are `ClassicAspxInventories` and `ClassicAspxItemBindings`. Native Classic reports add:

| Output | Key fields |
|---|---|
| `aspxinventory.csv` | `ScanId`/`AspxKey`, identity kind/version, site/file GUIDs and locations; file read/errors; declared/normalized/effective Handler and read/parse/source/default/reason evidence; catalog source/version/digest/entry; `ProvenanceJson`, `CoverageState`, `FirstObservedAtUtc`, latest `ObservedAtUtc`, `DistinctItemBindingCount` and `BindingCountBasis`. |
| `aspxitembindings.csv` | Scoped item/resource key; optional `ItemGuid` and read state; item metadata; `BindingKinds`, `BindingProvenanceJson` and `ObservedAtUtc`. Reasons include `FileSelf`, `ListFormRenderer`, `ContentTypeRenderer`, `DocumentMetadataRenderer` and `PublishingPageLayout`. |
| `classicaspxinfo.txt` | Acquisition marker/scope and nullable-text encoding. Missing native marker + empty tables means `NotAcquired/Legacy`; missing marker + new observed rows (for example after resume) means `ObservedWithoutNativeMarker` / `UnknownContract`, not an invented `aspx-inventory/v1` contract. |

The canonical filenames are `aspxinventory.csv` and `aspxitembindings.csv`. The earlier names `classicaspxinventories.csv` and `classicaspxitembindings.csv` remain compatibility aliases with **exactly the same bytes**, copied from the same completed typed streams rather than another query. Nullable text in canonical and compatibility files uses the same BackslashNullV1 encoding.

`aspxsummary.json` uses `schemaVersion: "aspx-inventory-summary/v1"`. It records the assessment ID, native projection contract/acquisition state, requested scope, frozen template exclusions, catalog source/version/digest (never its payload), `observedInventoryCount`, `observedDistinctBindingCount`, `zeroObservedBindingsCount`, observed inventory coverage states and canonical-file lengths/SHA256 hashes/compatibility names. Counts describe the exported streams, not a second query or tenant-wide denominator. `HomePageOnly`, unknown/absent contract, unknown catalog and incomplete/changed coverage remain separate; the summary makes no tenant-completeness assertion. Zero observations in an upgraded legacy scan remain NotAcquired/Legacy, not proof of no ASPX.

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

Pause/restart retain assessment evidence and merge repeated observations by stable resource/item-pair keys. Reasons combine; first/latest timestamps and provenance preserve context. Location or Handler changes can report `ChangedDuringScan` rather than silently proving one immutable state. Metadata-only or denied observations do not erase already acquired source facts. These semantics do not promise service-side exactly-once execution, a transactional tenant snapshot or coverage of unreadable content.

## Saved-scan compatibility

New scans declare `AspxProjectionContract=aspx-inventory/v1` and `AspxScopeMode`. Saved preview declarations are recognized and normalized during export without backfilling properties or changing facts. When both property families exist, the current family is authoritative; unknown or incomplete current declarations never borrow an older version or scope. Retained raw evidence keys and pagination receipts are preserved during resume, and changed evidence remains uncertain rather than gaining complete coverage.
