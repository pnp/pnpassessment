# PublishingLayoutPage reference regression coverage

## Scope and authority

`PublishingLayoutReference.FinalizeAsync`, called by `AssessmentDiscoveryWriter.FinalizeScanAsync` in the existing post-scan phase, reads the stored `Scans.PublishingLayoutRuleVersion`. Only version 1 evaluates pending references. Version 0, absent authority and unsupported versions leave references untouched, including historical `InventoryPending` rows. No schema changes or migration edits are needed.

Corrected resolution consumes `PublishingLayoutTypeEvidence.IsConfirmedMember`, not ContentType or AssetPurpose. Matching requires unique independently discovered physical inventory in the same scan/site and normalized URL, with no conflicting available site collection IDs. Web identities need not match. Existing discovery/assessment failures and source-acquisition failures prevent a successful relationship. Unproven ancestry and proven non-members have distinct unresolved reasons. See [classicpages.csv](csv-classicpages.md) and [discovery.csv](csv-discovery.md) for the public field/value contract.

The resolver has no network client, source acquisition or body-analysis callback. It reads the stored scan authority, content-page references and supplied discovery inventory; it changes only reference status/reason. URL capture and normalization, outer-page metadata, body/Web Part processing and the dependency are unchanged. A defensive pending-URL check prevents two unusable locators from matching. It preserves the stored URL rather than inventing or repairing one.

## Superseded expectations and replacement coverage

- **PublishingLayoutReferenceTests positive fixtures** previously supplied only `PageLayout/Confirmed/PageLayoutContentType`. They now record version 1 and acquire source-declared evidence through the shared resolver. The cross-web, both processing orders, URL casing/escaping, Layout name, classification, analysis and native CSV checks remain. The success reason is now `ConfirmedPublishingLayoutFamily`, not historical `ConfirmedLayoutAsset`.
- **Unknown/other-purpose negative fixtures** now test incomplete ancestry and proven non-membership, retaining their known URL/unresolved assertions. Additional ContentType-only and missing-origin cases reject unconfirmed evidence. Separate directly persisted stale-purpose fixtures prove that `PageLayout/Confirmed` cannot resolve non-members, unresolved identities, incomplete ancestry or unavailable source even without a writer recomputing purpose first.
- **Outside-catalog positive cases** use root, direct and indirect subclass evidence from real synthetic ECMA-335 metadata, with no ContentType and deliberately unconfirmed purpose. Both page/target storage orders resolve, independently of catalog placement or purpose projection. Finalization leaves the target's entire discovery record unchanged.
- **Isolation and failures** retain different scan/site URL/site ID, absent target, nonphysical reference row and ambiguous locator cases. Optional absent site IDs still permit a URL/site match when no available IDs conflict. Existing discovery/assessment Denied/Failed/Unknown checks remain. Source-acquisition failures remain after later successful observations, with evidence JSON retained. Reference failures are tested both before and after acquisition of a usable URL.
- **URL and native acquisition regressions** retain every existing normalization case and missing/unusable/conflicting URL check. The native PnP metadata replay now records version 1 and supplies a source-confirmed target instead of ContentType-only confirmation. Original URL-field, query/request-count, friendly-name, classification and enrichment-input assertions remain; target metadata-stream requests are additionally asserted to be zero.
- **AssessmentLayoutAccountingReplayTests** retain every scenario and root/direct/indirect routing case. Successful relationship reasons now identify family confirmation. The former unknown-purpose scenario now reports `TargetTypeSourceUnknown`, because the unavailable source is the authority failure, not absent ContentType. All inventory, failure, envelope declaration/delegation, analysis, counters, rollups and native CSV assertions remain. Metadata-request counts are unchanged by finalization, and matched targets must satisfy the shared predicate.
- **Database/native CSV and history** retain the pre-reference-schema upgrade/default checks. Added upgrades from the ContentType-based reference schema preserve pending, resolved and denied historical references and export them unchanged. Actual StorageManager launch/consolidation/restart tests prove that version 1 resumes corrected resolution, while version 0 and unsupported versions preserve pending references; repeated finalization is stable. Existing resolved historical fixtures remain historical, not acceptance evidence for corrected membership.

No test is removed, skipped or disabled. The existing source-evidence and routing suites continue to cover independent discovery, raw-source inspection and content-page admission; these reference tests do not introduce a second classifier or claim runtime-handler observation.

## Actual verification

Run from the repository root on 2026-09-28:

```text
dotnet test src/PnP.Scanning/PnP.Scanning.Core.Tests/PnP.Scanning.Core.Tests.csproj -c Release
```

Final result: **385 passed, 0 failed, 0 skipped**, exit 0. The focused rerun below passed **121 tests, 0 failed, 0 skipped**, exit 0:

```text
dotnet test src/PnP.Scanning/PnP.Scanning.Core.Tests/PnP.Scanning.Core.Tests.csproj -c Release --filter 'FullyQualifiedName~PublishingLayoutReferenceTests|FullyQualifiedName~AssessmentPageMetadataReplayTests|FullyQualifiedName~PublishingLayoutTypeUpgradeTests|FullyQualifiedName~ClassicLayoutUpgradeTests'
```

An initial focused run passed 81 tests before the final additions. The first full run had 384 passed and one failure: the native URL-field replay still used an unversioned ContentType-only target fixture. Updating that fixture to version 1 and source proof, without removing or weakening any of its assertions, produced the final passing results above. Existing dependency NU1902 (Microsoft.Build.Tasks.Git 8.0.0) and SourceLink warnings remain; they did not prevent execution. The dependency worktree remained clean at the pinned revision. These are offline synthetic/replay, SQLite, restart and native CSV checks, not live-tenant validation or upstream publication.
