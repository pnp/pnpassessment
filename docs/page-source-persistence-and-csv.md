# Native Page source evidence, CSV and rollback

This connects the [physical source reader](physical-aspx-source-acquisition.md)
and [Page declaration/default projection](page-directive-base-type-projection.md)
to the existing Classic assessment writer and `discovery.csv`. It adds no
scanner, CLI, generic PageArtifact catalog, deployment-configuration fetch,
reference-driven layout acquisition or server diagnostic job.

Fixtures are synthetic and offline. Passing them does not claim live/full-tenant
coverage, installed CLR types, successful execution or an observed request
Handler. Future live readback requires separate bounded authorization; the
original controlled test cohort, if used, must retain `testtraffic-smr`.

## Acquisition and durable storage

`ClassicPageDiscoveryComponent` uses `AspxSourceAcquisition.ForAssessmentScan`
inside the existing `AssessmentWebDiscovery` per-file callback. Existing PnP
authentication, scheduled Web context, physical download and TPL scope remain
unchanged. File existence and original discovery evidence are committed **before**
source acquisition and ListItem/body/Web Part admission. System, Form and View
files use the same boundary, with or without an Item.

Reusable reader/parser contracts remain transient, not EF entities.
`AssessmentDiscoveryWriter` maps them to one nullable, versioned
`ClassicPageDiscoveries.SourceEvidenceJson` document containing:

- `Version = 1`, original `DiscoveryObservations` and per-acquisition `Reads`;
- copied raw discovery records, paths/names, Site/Web/File IDs and optional
  List/Item identities, including missing/invalid raw strings;
- distinct read observation IDs, physical-source identity/path/name, source
  version or observation UTC time, identity-comparison states/reasons;
- original bytes as base64, actual captured length, optional expected length,
  raw digest algorithm/digest/scope and strict decoding evidence;
- independent transport, capture, decoding, content and lexical parse outcomes;
- exact `DeclaredInherits`, separate normalization, `BaseType`, `TypeSource`,
  reasons, reliable-parse/verified-absence flags and retained raw directives;
- configuration knowledge, applicability, provenance, raw file scope/evidence,
  explicit framework-default-assumption flag and Handler availability/reason.

Bytes live **in assessment.db**, not behind a mutable SharePoint URL or in an
untracked sidecar. There is no separate artifact table/catalog or discovery.
`SourceArtifactReference` is an assessment-local locator:

```text
assessment.db#cp1/<ScanId>/<URI-escaped RecordKey>/<ObservationId>
```

`AssessmentDiscoveryWriter.ReadSourceArtifactAsync(scanId, recordKey,
observationId)` opens a fresh context, checks recorded length and SHA256 and
returns a defensive byte copy. Unknown/unavailable captures return null, not
invented empty bytes. Corrupt/unsupported durable evidence fails closed rather
than being replaced by an empty observation.

Physical identity is the Site/Web/File tuple, not URL/name/Item number.
Rename/version changes preserve original discovery and every source observation.
Different File UniqueIds at one URL remain different rows. Unresolved discovery
keys are observation keys, not manufactured physical GUIDs.

Metadata-only updates, repeated writes, failure finalization and restart retain
the document. Distinct acquisitions append observations; idempotent replays do
not duplicate them. No observation is silently pruned. The reader's capture
bound remains 16 MiB; repeated captures can increase database/report size.
Decoded bodies are not duplicated: original bytes and bounded directive
evidence are retained.

## Independent states and compatibility

`SourceReadState=Complete` means transport completion, **not** reliable physical
source or parsing. Login/rendered HTML, decode failure or an observed empty
response can have complete transport. Consult capture, decoding, content,
parse/projection flags and the per-read JSON's `ReliableSource`.

Lexical `PageParseState=Declared` can retain a candidate from a denied/partial
response while `PageParseReliable=False`, `TypeSource=Unknown`, `BaseType=null`.
Lexical absence under unreliable acquisition does not set
`VerifiedInheritsAbsence`. Empty, unavailable and reliably absent declarations
are not interchangeable.

Every CSV convenience field selects the **same latest read**, not a blend of an
old successful type and a new failed read. Earlier successful bytes/declarations/
reasons remain in the document. Source/parser errors add no whole-page skip,
erase no other successful facet, change no file enumeration coverage and block
no unrelated file. Database write failures still fail the worker.

Inherited family merge/predicate, routing, accounting and layout-reference
behavior stay independent. Content Type, Layout and Web Part profiles cannot
write direct declaration evidence. `FrameworkDefault` never strengthens family
proof; a reliable Page without Inherits retains family `Unknown` and the inherited
`InheritsMissing` reason. No declaration/default populates an observed Handler
type. Availability is `ServerOnlyUnavailable` with
`ServerDiagnosticsNotCollected;PhysicalSourceIsNotAnObservedRequestHandler`.

## Frozen new-scan provenance

Only `StorageManager.LaunchNewScanAsync` enables
`Scans.PageSourceEvidenceVersion = 1` and freezes
`Scans.PageBaseTypeConfigurationJson` from the existing
`PageInherits:PagesPageBaseTypeEvidence` section. No supplied evidence still
produces a versioned empty snapshot: deployment knowledge remains Unknown, not
verified no-override. The projection contract defines supported exact-file
scope, knowledge states and provenance requirements.

Restart restores the recorded version/snapshot, not mutable settings. Explicit
declarations precede defaults. Configured defaults require applicable provenance/
scope and reliable absence; framework defaults remain explicit assumptions, not
family/Handler proof. CP1 projection and inherited family rule versions are
separate gates.

Inherited scans, including family-rule-version-1 scans, gain **no CP1 authority**
on upgrade/restart. The new version defaults to 0 and configuration/document
fields to null. Their new fields remain uncollected. Existing acquisition/family
behavior can still run under its recorded policy, but is not retroactive raw-byte/
default evidence. Old declarations, classifications, failures, results and
decoded-text `SourceHash` remain recognizable; none is converted to raw digests
or successful CP1 reads.

## Additive native CSV

The existing writer emits `discovery.csv` with the same original 39 columns,
order, meanings, rows and filenames. `PageType` remains ignored there. Other
Classic report files are unchanged. These columns are appended:

| Group | Added columns |
| --- | --- |
| Full evidence / original discovery | `SourceEvidenceJson`, `SourceEvidenceState`, `OriginalDiscoveryUrl`, `OriginalDiscoveryFileName`, `OriginalDiscoveryObservedAtUtc` |
| Physical read identity | `SourceObservationId`, `SourceSiteCollectionId`, `SourceWebId`, `SourceFileUniqueId`, `SourceListId`, `SourceListItemId`, `SourceUrl`, `SourceFileName`, `SourceIdentityState`, `SourceIdentityReason` |
| Version / time | `SourceVersionState`, `SourceVersionReason`, `SourceETag`, `SourceMajorVersion`, `SourceMinorVersion`, `SourceObservedAtUtc` |
| Bytes / digest | `SourceArtifactReference`, `SourceCapturedByteLength`, `SourceExpectedByteLength`, `SourceRawDigestAlgorithm`, `SourceRawDigest`, `SourceRawDigestScope`, `SourceEncoding` |
| Acquisition | `SourceReadState`, `SourceReadReason`, `SourceCaptureState`, `SourceDecodingState`, `SourceDecodingReason`, `SourceContentState`, `SourceContentReason` |
| Declaration / projection / parse | `DeclaredInherits`, `NormalizedInherits`, `BaseType`, `TypeSource`, `BaseTypeReason`, `FrameworkDefaultAssumption`, `PageParseState`, `PageParseReason`, `PageParseReliable`, `VerifiedInheritsAbsence` |
| Configuration | `ConfigurationKnowledgeState`, `ConfigurationApplicability`, `EffectivePagesPageBaseType`, `PageBaseTypeProvenance`, `ConfigurationReason`, `ConfigurationEvidenceJson` |
| Handler availability | `ObservedHandlerState`, `ObservedHandlerReason` |

`SourceEvidenceState` distinguishes `NotCollected`, `DiscoveryOnly`, `Collected`
and `NotApplicable`. Empty scalar CSV fields alone cannot distinguish null,
observed empty and unavailable: use explicit parse/capture states and JSON
null/empty values. Reliable missing Inherits, `Inherits=''`, denied reads and
zero-byte responses have different states. A CSV empty `SourceEvidenceJson` reads
back as null; an invalid nonempty database document is not treated as uncollected.

**The database and this CSV include sensitive physical source:** the full
document includes base64 original bytes. Keep real assessment artifacts,
private mappings, credentials and tool/cache contents outside public Git and
publication. Exporting is not permission to publish source.

Raw SHA256 hashes captured bytes including BOMs; partial capture is labeled
`PartialCapturedBytes`, not a whole-file digest. Complete captured response is
not atomic version attestation. Legacy `SourceHash` remains
`DecodedTextUtf8Sha256`. Unknown lengths remain null, observed zero remains zero.

New timestamp strings are invariant UTC with **seven fractional digits** and
literal `Z` (`yyyy-MM-ddTHH:mm:ss.fffffffZ`); offsets convert to UTC. Original
`ObservedAtUtc` retains existing CsvHelper DateTime formatting/second precision.
Commas, escaped quotes, CR and LF are preserved. Tests independently parse CSV
characters and hand-list expected headers/values, not product-serializer
expectations.

## Reviewed migration and backups

`20261006160012_ClassicPageSourceEvidence` follows
`20260928091529_ClassicPublishingLayoutTypeEvidence`. Generated Up has exactly
three AddColumn operations:

- nullable `Scans.PageBaseTypeConfigurationJson` (`TEXT`);
- `Scans.PageSourceEvidenceVersion` (`INTEGER NOT NULL DEFAULT 0`);
- nullable `ClassicPageDiscoveries.SourceEvidenceJson` (`TEXT`).

There is no repair/backfill, table drop/rename, catalog or historical migration
regeneration. The inherited `TestDelays` table stays mapped in Release and Debug
to prevent an unrelated scaffolded drop; debug-scanner collection/execution
remains conditional elsewhere. A Process design-time factory and `EF.IsDesignTime`
startup guard avoid CLI version checks/authentication/scanner startup during EF
tooling (EF 8 probes the entry point even with a factory). Core project, Process startup and
`Storage/DatabaseMigration` output conventions remain unchanged.

Use temporary **local** dotnet-ef **8.0.3**, never a global tool. Review operations
and provider SQL before applying; do not regenerate prior migrations.

Back up the database **with all original bytes and configuration**. This version
has no raw-byte sidecars: bytes are embedded in the document. Use SQLite's
consistent backup API (`SqliteConnection.BackupDatabase` in the fixture) or stop
all writers, checkpoint/close WAL and copy a verified closed database. Blindly
copying only the main file of an active WAL database is not a valid backup.
Restore schema/history, frozen configuration and bytes together; reopen, retrieve
artifacts and independently recompute length/SHA256.

## Rollback recipe

1. Unwind later dependent consumers first, including future layout binding/
   reference-driven acquisition and integrated changes. Do not remove a shared
   foundation while its consumers remain.
2. Stop writers and verify a consistent backup. Test downgrade/restore on an
   isolated copy first.
3. Using local EF 8.0.3 and current binaries, downgrade to the inherited
   `20260928091529_ClassicPublishingLayoutTypeEvidence` schema with an explicit
   connection for the intended copy:

   ```text
   <local-dotnet-ef> database update 20260928091529_ClassicPublishingLayoutTypeEvidence --project PnP.Scanning.Core --startup-project PnP.Scanning.Process --configuration Release --no-build --connection "Data Source=<database-copy>"
   ```

4. Down removes exactly the three CP1 columns. It discards **all embedded
   original/partial bytes, discovery snapshots, per-read identities/versions/
   times, raw digests/encoding, CP1 declarations/defaults, parse/projection/
   configuration/Handler-availability evidence and frozen scan CP1 version/
   configuration**. Inherited family/text-hash, discovery/results/routing/
   reference columns remain. Up again creates null/uncollected fields and
   version 0; it cannot recover discarded evidence.
5. SQLite rebuilds affected tables on Down and may reorder physical columns.
   Address old columns by name, not `SELECT *` order. Native CSV retains its
   original header order.
6. Revert only the persistence delta to its recorded input baseline, or unwind
   all CP1 producers in reverse dependency order to the declared historical
   integration baseline. Record exact pins in operational evidence; do not
   reset shared/checkpoint refs or change the read-only dependency. Rebuild and
   rerun the original Release suite there.
7. To recover CP1 evidence, restore the verified pre-downgrade backup with
   matching code, reopen and compare retrieved bytes/digests. Forward migration
   of a downgraded database is not restore.

## Offline reruns

From `src/PnP.Scanning`, with authorized local .NET/ASP.NET Core 8 runtime host,
unchanged supplied SDK, read-only dependency mapping and external per-project/
per-framework outputs:

```text
dotnet build -c Release
dotnet test -c Release --logger 'trx;LogFileName=cp1-release.trx'
dotnet test PnP.Scanning.Core.Tests/PnP.Scanning.Core.Tests.csproj -c Release --filter 'Category=PageInherits' --logger 'trx;LogFileName=cp1-fixtures.trx'
dotnet test PnP.Scanning.Core.Tests/PnP.Scanning.Core.Tests.csproj -c Release --filter 'FullyQualifiedName~PageSource' --logger 'trx;LogFileName=cp1-native.trx'
```

`PageSourcePersistenceTests`, `PageSourceCsvTests`, `PageSourceRestartTests`,
`PageSourceUpgradeTests` and `PageSourceNativePipelineTests` exercise native
reopen/retrieval, independent CSV, per-observation merges, failure isolation,
frozen/legacy authority, historical Up/Down/Up and WAL backup/restore. Existing
acquisition, parser/default, family, discovery, assessment, routing/accounting
and report regressions remain intact.

Operational evidence records actual commands (including runtime-host runsettings
and output mapping), tested commit, environment, UTC times, exit codes, counts,
logs/TRX, reviewed SQL and concrete rollback pins. These belong in the private
delivery area, not public Git.
