# discovery.csv file details

## Summary

This csv file contains the physical ASPX inventory and acquisition evidence collected within the requested page scope. The existing assessment ScanId, database and report lifecycle own every row. A normal classic pages assessment uses the full physical inventory scope. With `--homepageonly`, discovery resolves only each web's configured welcome page so the scan remains a fast home-page assessment.

`Url` identifies each physical page, while `FileName` provides its leaf name directly. Final page classification belongs to [classicpages.csv](csv-classicpages.md); discovery rows retain the physical identity, acquisition coverage and assessment disposition needed to explain which files did or did not produce classic-page results.

`Page` rows represent physical ASPX files. `Scope` rows represent tenant, site, web, list, folder and API surfaces. `Reference` rows record Forms, Views and welcome-page references. `Pagination` rows record sanitized request-chain evidence. `Gap` rows retain acquisition gaps that are not owned by one scope. After post-scan processing, the `Summary` row records the scan-wide coverage verdict.

## File purpose versus content pages

For scans with recorded `Scans.PublishingLayoutRuleVersion = 1`, content-page admission uses the physical file's own source-declared CLR-family evidence below. ContentType, catalog location, a layout selection and a delegated handler cannot confirm layout membership. The source is inspected before routing, including root/catalog files with no list item. A ContentType-based purpose from an earlier observation is not type evidence.

A physical ASPX is not necessarily a content page. `AssetPurpose`, `AssetPurposeStatus` and `AssetPurposeReason` describe file purpose independently of page-family evidence and runtime classification. For Page rows:

Purpose | Status | Evidence
--------|--------|---------
`PageLayout` | `Confirmed` | `PublishingLayoutTypeEvidence.IsConfirmedMember`: source-declared, available, resolved evidence of the exact PublishingLayoutPage root or a proven direct/indirect subclass. Reason: `ConfirmedPublishingLayoutFamily`. No ContentType is required.
`ContentPage` | `Confirmed` | Proven available source-declared non-membership **and** a recognized Publishing, modern site page, basic/web part or wiki page content-type family. Reason: `ContentPageContentType`. Non-membership alone does not establish content-page purpose.
`Unknown` | `Unknown` | Unproven type ancestry, or no recognized content-page purpose metadata. Reasons include `TypeFamilyNotContentPurpose`, retained type-resolution reason codes, `ContentTypeUnavailable`, `UnrecognizedContentType` and `ContentTypeNotTypeEvidence` (the Page Layout content type is metadata, not CLR evidence).

Confirmed family files remain Page rows with their scan, site, web and file identities, ContentType and type evidence, even outside `_catalogs/masterpage`. They cannot retain a `PublishingPage` metadata projection. Their normal assessment disposition is `ExcludedAsset`: they are not content-page results in `classicpages.csv`, and are not inputs to content-page body/Web Part analysis, remediation, page counters or publishing rollups. The discovery Summary's physical file count still includes them. Do not use that inventory count as a content-page count. Admission occurs before list-item loading and enrichment, not as a report filter.

Purpose confirmation does not imply successful discovery or assessment. Existing `Denied`, `Failed` and `Unknown` statuses and error details remain unchanged by exclusion or later successful observations. Repeated observations merge type evidence before recomputing purpose; a sticky metadata-derived `PageLayout/Confirmed` or `ContentPage/Confirmed` is never restored over Unknown type evidence. Type acquisition failures and conflicting observations remain in the type fields and observation JSON. A resolved non-member with Page Layout ContentType is not excluded. Unknown purpose does not suppress existing selection, PageType classification or eligible content-page assessment behavior, and successful page assessment does not confirm its purpose.

Version `0` retains the historical ContentType admission rule (case-insensitive Page Layout content type `0x01010007FF3E057FA8AB4AA42FCB67B453FFC1` and descendants, with `PageLayoutContentType` reason). Historical stored results are not reclassified, reanalysed, deleted or filtered at export. Restart uses the stored rule version rather than upgrading authority. The layout-reference evaluator consumes the same confirmed-family predicate for version `1`, not purpose metadata. Legacy references, including `InventoryPending`, remain unchanged during finalization. See [classicpages.csv](csv-classicpages.md) for the relationship contract and explicit unresolved reasons for selected non-family or unconfirmed targets.

The persisted contract is the `ClassicPageDiscoveries` table's `AssetPurpose`, `AssetPurposeStatus` and `AssetPurposeReason`, exported under the same names. Use `ScanId` plus site identity (`SiteUrl`, and `SiteCollectionId` when available) and the Page row's server-relative `Url` to associate a file with other same-scan, same-site results. `RecordKey`, `FileUniqueId`, `WebId`, `ListId` and `ListItemId` remain physical discovery identities; catalog assets may belong to a different web from a referring page. Purpose does not establish a reference relationship by itself.

Added columns default to `Unknown`, `Unknown`, `NotEvaluated` in existing databases and on non-file evidence rows. Upgrades do not repair historical classifications, assessment values or reports. Interpret purpose only on Page rows with evaluated evidence.

## Source-declared PublishingLayoutPage family evidence

For scans with `Scans.PublishingLayoutRuleVersion = 1`, the Classic discovery worker downloads each independently discovered physical ASPX file through its existing assessment PnP context, including files without list items. The row is committed before the download. `GetFileByServerRelativeUrlAsync` obtains its `UniqueId`; a changed discovered identity fails the source read. PnP Core 1.18.0 `IFile.GetContentAsync(true)` downloads by UniqueId through `/_layouts/15/download.aspx` on .NET (the browser implementation uses the file `$value` endpoint). This is the file content stream, not navigation to the rendered ASPX. There is no layout-reference-driven fetch, page execution, handler observation or combined page/layout body analysis. Inspection reads only the leading ASP.NET directives; it does not analyze Web Parts or the page body.

`@Page Inherits` supplies a declared type, not a runtime observation. Exactly one leading Page directive is required; whitespace, a BOM, other leading directives and ASP.NET server comments are supported. Attributes must be quoted. Missing, duplicate or malformed Page directives/attributes remain Unknown. `CodeFile`/`Src` compilation, inline type definitions and runtime handler delegation are not ancestry evidence.

### Authoritative evidence and identity binding

The rule has two fixed identity anchors:

- `Microsoft.SharePoint.Publishing.PublishingLayoutPage` in `Microsoft.SharePoint.Publishing, Version=15.0.0.0` or `16.0.0.0`, `Culture=neutral, PublicKeyToken=71e9bce111e9429c`. These exact root identities define the supported family, not a name prefix or a hard-coded subclass list. Evidence provenance is `WellKnownPublishingLayoutIdentity:v1`.
- `System.Object, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089` terminates a proven outside-family chain. Provenance is `WellKnownSystemObjectIdentity:v1`. Merely failing to reach the publishing anchor is **not** proof of non-membership.

Additional direct/indirect subclass and outside-family proof comes from ECMA-335 `TypeDefinition.BaseType` metadata in operator-supplied copies of the assemblies deployed for the assessed pages. Configure the existing scanner process's `appsettings.json` (or its standard .NET configuration providers), for example:

```json
{
  "PublishingLayoutTypeEvidence": {
    "Assemblies": [
      "C:/assessment-evidence/Deployed.Pages.dll",
      "C:/assessment-evidence/Deployed.BasePages.dll"
    ]
  }
}
```

No assembly is loaded/executed, no file is fetched from SharePoint because it is an assembly reference, and no new CLI is required. Supply matching deployed artifacts and their required base assemblies, not arbitrary same-named binaries. The operator is responsible for deployment correspondence; metadata inspection is not signature verification or remote runtime attestation. The scanner reads each configured file once at new-scan creation, records its full assembly identity and SHA-256 of those same bytes, and freezes the extracted graph in `Scans.PublishingLayoutTypeCatalogJson`. Local file paths and code bodies are not persisted. A missing or invalid artifact is recorded in the catalog's `Errors` and copied to observation evidence; it does not fabricate a type. With no configuration, only the fixed identity anchors are available. The frozen snapshot makes restart independent of subsequently changed files or configuration.

Binding requires a simple fully assembly-qualified Inherits identity: type name, assembly name, Version, Culture and PublicKeyToken (including explicit `null` for unsigned assemblies). `AssemblyName` canonicalizes the assembly string; matching is otherwise exact and case-sensitive, with no version unification, probing, aliases or binding redirects. Every base edge must bind exactly to a definition in the snapshot or a fixed anchor. A base `TypeReference` supplies its own `AssemblyReference` identity; a local `TypeDefinition` uses the containing assembly identity. Bare/partially qualified names, unsupported versions, nested/generic types, `TypeSpec` bases, multi-module resolution and forwarded types remain unresolved rather than guessed. Cycles, duplicate identity definitions with conflicting bases, incomplete chains and conflicting observations remain Unknown with reasons. A complete chain reaching the publishing anchor proves `Member`; one reaching the Object anchor without that family proves `NonMember`. Other terminal definitions do not prove non-membership.

ContentType (including descendants), catalog paths, layout selection, similar class names and changes to `HttpContext.Current.Handler` never enter this resolver. A TemplateRedirectionPage's selection/delegation cannot replace the outer file's Inherits declaration; resolving that declared type needs its own authoritative chain like any other non-anchor type.

### Shared persistence and scan-version contract

`ClassicPageDiscoveries` and native `discovery.csv` use identical field names:

Field | Values and meaning
------|-------------------
`ContentTypeId` | Retained original metadata, now explicitly exported; not CLR evidence.
`DeclaredPageType` | Inherits value; distinct declarations from repeated observations are newline-separated. Null if none could be parsed. Raw ambiguous directives remain in observation JSON.
`ResolvedPageType` | Canonical assembly-qualified declared identity when the anchor/definition is available, even if its ancestry is incomplete. Null for unresolved or conflicting identities.
`PageTypeEvidenceOrigin` | `None` for unevaluated rows; `DeclaredSource` for source-acquisition/inspection evidence. Neither value claims runtime observation.
`PageTypeSourceStatus` | `Available`, `Denied`, `Failed`, or `Unknown`. Available source can still have unresolved type evidence. Missing source is Unknown; an empty downloaded file is Available but has `SourceUnavailable` and Unknown resolution.
`PageTypeResolutionStatus` | `Resolved` only for proven membership/non-membership; otherwise `Unknown`.
`PublishingLayoutFamily` | `Member`, `NonMember`, or `Unknown`; separate from `PageType`, purpose, discovery status and assessment disposition.
`PageTypeReason` | Distinct semicolon-separated reason codes; default `NotEvaluated`.
`PageTypeEvidenceJson` | Retained array of observations: `Declaration`, `ResolvedIdentity`, `SourceHash`, `SourceStatus`, `ResolutionStatus`, `Decision`, `Reason`, `Ancestry`, `CatalogErrors`, `DirectiveEvidence`. Each ancestry edge has `Identity`, `BaseIdentity`, `Provenance`. ECMA-335 provenance includes full assembly identity and artifact SHA-256. `SourceHash` hashes the decoded source text as UTF-8 (not the original download bytes). No page body is retained.

Reasons include `RootOrProvenSubclass`, `ProvenOutsideFamily`, `SourceUnavailable`, `PageDirectiveMissing`, `AmbiguousPageDirective`, `AmbiguousPageAttribute`, `MalformedPageDirective`, `InheritsMissing`, `DynamicCompilationUnsupported`, `UnresolvedIdentity`, `IncompleteAncestry`, `ConflictingAncestry`, `AncestryCycle`, `ConflictingTypeObservations`, `DirectiveInspectionTimedOut`, and `SourceDenied:<error-code>` / `SourceFailed:<error-code>`. An HTTP or acquisition exception does not remove the file or convert it into an empty result.

`PublishingLayoutTypeEvidence.IsConfirmedMember(row)` is the shared positive predicate: Page row, DeclaredSource origin, Available source, Resolved resolution and Member family. Consumers must first check the scan's recorded rule version; do not use AssetPurpose or ContentType as a substitute. NonMember is not proof of content-page purpose. Unknown is never a positive layout or content-page confirmation.

Only `StorageManager.LaunchNewScanAsync` initializes rule version `1` and captures the catalog. CLR/EF defaults and the additive migration give existing scans version `0` and a null catalog. Discovery reads the recorded scan on every worker; `ForScan` enables inspection only for version `1`, not version `0` or an unsupported future version. `AssessmentWebDiscovery`, `AssessmentDiscoveryWriter` and `PageScanComponent` consume that same persisted version for purpose projection, merges and routing. `PublishingLayoutReference` checks the stored version before finalizing any pending relationship and uses the shared confirmed-family predicate against existing same-scan, same-site inventory after all web workers finish. Restart and consolidation do not assign a new authority or reopen assembly files. There is no backfill of existing discovery rows, classifications, references, analyses or reports.

New discovery fields default to null, `None`, `Unknown` and `NotEvaluated`; non-file coverage rows do not receive source evidence. Repeated acquisitions accumulate distinct observations. A retained Unknown/Denied/Failed observation is not replaced by a later successful one; contradictory identities/decisions force Unknown. Source status precedence is Denied, Failed, Unknown, Available, with all individual states/reasons retained in JSON. Assessment-only writer updates preserve the history. Coverage failures and their unknown counts remain independent of family membership and cannot become Complete/Empty/zero on reobservation.

> [!NOTE]
> A finished assessment can contain `Denied`, `Failed`, `Partial` or `Unknown` coverage rows. These rows identify parts of the requested scope with incomplete inspection. Discovered pages remain as Page rows when their assessment fails. `discovery.csv` records discovery and assessment gaps through Scope and Gap rows together with the `ErrorStage`, `ErrorCodes` and `ErrorDetail` columns. The Summary evidence records `pageScope=HomePageOnly` for a scoped home-page scan and `pageScope=FullInventory` otherwise.

## Summary coverage verdicts

The `Summary` row stores the scan-wide coverage verdict in `DiscoveryStatus`. Verdict evaluation follows the order shown below, so uncertainty and incomplete coverage take precedence over successful scope classifications.

Verdict | Condition
--------|----------
`Unknown` | No Scope rows were recorded, `assessment:site-selection` is absent or unrecognized, a coverage row remains `Pending` or `Unknown`, or an acquisition gap prevents the scanner from establishing the observed denominator or identity.
`Incomplete` | A coverage row is `Denied`, `Failed`, `Partial` or `Cancelled`, or an expected child scope was not observed.
`CompleteDeclaredSubset` | All recorded coverage succeeded and the assessment used an explicit site selection or `HomePageOnly` page selection.
`CompleteTenantVerified` | All recorded coverage succeeded and `assessment:site-selection` records a completed `Tenant` enumeration.

## Columns

The following columns are included:

The original columns and their order remain unchanged. CP1 appends physical-source,
byte/digest, declaration/default, configuration and independent state fields through
this same native writer. See [native Page source evidence and CSV](../page-source-persistence-and-csv.md)
for the appended headers, UTC precision, null/empty/unavailable distinctions,
sensitive embedded bytes, backup and rollback. There is no separate exporter
or extra discovery row type.

Column|Description
------|-----------
RecordKey | Stable key for the page or discovery scope within the assessment.
RowType | `Page`, `Scope`, `Reference`, `Pagination`, `Gap` or `Summary`.
ScopeType | Type of scope or object represented by the row, such as `Tenant`, `SiteCollection`, `PageSelection`, `Web`, `List`, `Folder`, `Surface` or `File`.
ParentScopeKey | Record key of the parent discovery scope.
Url | Server-relative page URL for a Page row. For a Scope row this is the inspected scope or endpoint.
SiteCollectionId | Id of the owning site collection when it could be resolved.
WebId | Id of the owning web when it could be resolved.
ListId | Id of the owning list or library when the row is list-backed.
FolderUniqueId | Unique id of the owning or inspected folder when available.
FileUniqueId | Unique id of the discovered file for Page rows when available.
ListItemId | List item id of the discovered file when the page is list-backed.
FileName | File name of the discovered page.
AssetPurpose | `PageLayout`, `ContentPage` or `Unknown`; file purpose, not runtime or page-family classification.
AssetPurposeStatus | `Confirmed` or `Unknown`; confidence in the purpose decision, not acquisition success.
AssetPurposeReason | Semicolon-separated purpose evidence codes described above; `NotEvaluated` for historical/default rows without an evaluation.
ContentTypeId | Retained content-type identifier; metadata only, not type-family proof.
DeclaredPageType | Source-declared Inherits value(s), as specified in the shared contract above.
ResolvedPageType | Canonical assembly-qualified identity, or empty when unresolved/conflicting.
PageTypeEvidenceOrigin | `None` or `DeclaredSource`; never a runtime observation.
PageTypeSourceStatus | Source acquisition state, independent of type resolution.
PageTypeResolutionStatus | `Resolved` or `Unknown`.
PublishingLayoutFamily | `Member`, `NonMember` or `Unknown` for the narrow PublishingLayoutPage family.
PageTypeReason | Retained type/acquisition reason codes.
PageTypeEvidenceJson | Source-declared observation history and authoritative ancestry provenance.
HomePage | True or False when the web's welcome page could be resolved and compared with this page. Empty means the home-page state is unknown.
LibraryHidden | True when the owning library is hidden, False when it is visible, or empty when this could not be determined.
ObservationMethod | API surface or adapter that observed the page or scope.
DiscoveryStatus | Page existence, scope completion, reference disposition, pagination completion, gap state or the final scan-wide coverage verdict, depending on `RowType`. The Summary values and their conditions are defined in Summary coverage verdicts.
AssessmentStatus | Assessment disposition: `Complete`, `NotSelected`, `NotApplicable`, `ExcludedAsset`, or retained failure/uncertainty (`Denied`, `Failed`, `Unknown`). Empty for Scope rows. `ExcludedAsset` does not replace a retained failure or uncertainty status. A home-page-only run discovers only the selected welcome pages rather than emitting every other page as `NotSelected`.
ExpectedChildCount | Number of child scopes or objects expected when the source can provide a reliable count.
ObservedChildCount | Number of child scopes or objects that were observed.
ErrorStage | Discovery or assessment stage that produced an error or coverage gap.
ErrorCodes | One or more error or coverage-gap codes.
ErrorDetail | Details associated with `ErrorCodes`.
EvidenceJson | Structured surface, reference, pagination or summary evidence. Continuation values are represented by hashes rather than raw tokens.
ObservedAtUtc | UTC timestamp at which the page or scope was recorded.
ScanId | Id of the assessment.
SiteUrl | Fully qualified site collection URL associated with the row.
WebUrl | Relative URL of the web associated with the row.
