# Physical Page source evidence and integrated regressions

Classic assessment can retain a physical ASPX source observation, its exact
`@Page Inherits` declaration and a declared/configured/framework base-type
projection. These are **source facts and explicit assumptions**, not observed
request execution, deprecation impact or proof of an installed CLR type.

The implementation extends the existing Assessment entry path, writer,
`assessment.db` and `discovery.csv`. There is no second scanner, command-line
entry point, exporter, artifact catalog, configuration-fetch job or server job.
Existing authentication, scheduled Web/TPL scope and PnP physical-file download
are reused. Discovery commits existence before source enrichment; list-item,
body and Web Part admission comes later. System, Form and View files use this
same rule, with or without a list item. A reference in the downloaded text does
not expand discovery or authorize another download.

## Reading a result

1. **Identity and version:** physical identity is the Site collection/Web/File
   UniqueId tuple. URL/path and name are mutable locators, not unique identity.
   List/Item identity is optional. Original discovery and every source
   observation remain separate through rename, version changes, repeated
   acquisition and replacement of a file at one URL. Missing identities or
   version metadata retain explicit states/reasons; observation UTC time is
   retained when no version was returned. Metadata-before-download is not an
   atomic server snapshot.
2. **Bytes:** the bounded reader saves the bytes it actually captured, including
   BOMs, actual length, SHA256 and digest scope. The database contains these
   bytes; the assessment-local artifact locator is not a live URL. Reopening
   and retrieving an artifact checks length/digest. A null capture is not a
   zero-byte response. `PartialCapturedBytes` is not a whole-file digest.
3. **Decoding:** strict UTF-8 (including the explicit BOM-less assumption) and
   BOM-selected UTF-16/UTF-32 decoders reject malformed sequences rather than
   substituting replacement characters or guessing a legacy code page. Transport,
   capture, decoding and payload recognition have independent states/reasons.
4. **Declaration and defaults:** one reliably parsed leading `@Page` directive
   supplies exact `DeclaredInherits`; normalization is separate. Names/casing,
   attribute order, whitespace, line breaks and quoted bare or
   assembly-qualified values are supported. Body strings, comments and
   Master/Register directives are not Page declarations. Equal/case-variant
   duplicate attributes, multiple Page directives, empty Inherits and malformed
   or misplaced directives suppress inference and retain lexical evidence.
5. **Precedence:** an explicit valid declaration wins over supplied
   configuration. Only complete, reliable physical-source acquisition and
   reliable absence parsing permit defaults. An applicable frozen
   `pages.pageBaseType`, with provenance and exact-file scope, gives
   `ConfiguredDefault`. Otherwise `System.Web.UI.Page`/`FrameworkDefault` is
   explicitly an assumption. Unknown deployment configuration is **not**
   verified no-override; conflicting/unsupported evidence suppresses the
   default rather than selecting by order. `NotDeclared` is not a BaseType.

Raw SHA256 hashes captured bytes. The inherited `SourceHash` is a decoded-text
UTF-8 hash (`DecodedTextUtf8Sha256`). Equal decoded text with/without a BOM, or
in different encodings, can share the legacy text hash but must have different
raw-byte digests. Neither hash supplies an observed request Handler.

`Complete` transport is not sufficient evidence of a reliable source, a
successful parse or a projected type. HTTP 401/403, semantic denial/login shells,
failed or unreturned reads, rendered HTML, unknown payloads, unreliable decoding,
zero-byte/whitespace responses and partial/truncated source all suppress inferred
defaults. A retained lexical declaration in a denied/partial response is not a
verified type. Cancellation propagates; it is not successful unblocking or an
ordinary successful observation.

Failures remain independent. A failed source facet can coexist with successful
list-item/body assessment; a failed item facet can coexist with successful source
bytes/declaration. Source/parser failure adds no new whole-page skip, does not
change completed file enumeration into a Web skip, and blocks no unrelated file.
Metadata-only writes and profiles cannot overwrite direct source evidence.

## Family and Handler are separate

A reliable Page without Inherits can have `FrameworkDefault` while the inherited
narrow `PublishingLayoutFamily` is still `Unknown` with `InheritsMissing`.
Configured defaults do not strengthen this family predicate either. A bare
custom declaration or one using dynamic compilation can have `Declared`
projection while CLR-family proof remains unresolved.

No declaration, default, Content Type, layout selection or Web Part profile is
an observed request Handler. Without separately authorized server diagnostics,
the result is `ObservedHandlerState=ServerOnlyUnavailable` and
`ServerDiagnosticsNotCollected;PhysicalSourceIsNotAnObservedRequestHandler`.

## Native CSV and historical compatibility

The same native writer appends source fields after the original 39
`discovery.csv` columns, preserving their order, meanings, rows and filenames.
Other Classic report files are unchanged. The original ignored `PageType` field
does not become a discovery CSV column.

Each scalar convenience selects the **same latest read**. It never combines an
old successful type with the bytes/states of a new failed read.
`SourceEvidenceJson` retains all earlier observations and their independent
outcomes. An empty scalar field alone cannot distinguish unavailable, null and
observed empty: consult parse/capture states and JSON values. For example:

| Input | Parse/projection | Bytes |
| --- | --- | --- |
| Reliable Page without Inherits | VerifiedAbsent; eligible default | Captured; recomputable raw digest |
| `Inherits=''` | EmptyInherits; Unknown type | Captured |
| Returned zero-byte stream | EmptySource; Unknown type | Length 0; empty-byte digest |
| Unreturned/denied source | SourceUnavailable; Unknown type | Null capture, not invented empty bytes |
| Discovery-only observation | No source result yet | No claimed source capture |

New UTC strings use seven fractional digits and literal `Z`; original
`ObservedAtUtc` retains its existing CSV formatting. Exact raw attribute values,
configuration provenance, commas, escaped quotes, CR and LF survive native
export and independent CSV reading.

Only new-scan initialization freezes source-evidence version and supplied
configuration. Restart uses that recorded snapshot, not mutable settings.
Upgrade does not invent historical bytes/digests, successful states, default
types or applicable configuration. Old family/text-hash fields and old results
remain recognizable and do not gain new authority.

**The database and CSV may contain sensitive physical source**, including base64
bytes. Export does not authorize publication. Real artifacts, tenant information,
private evidence mappings, credentials and tool/cache contents stay outside Git.

See [the complete native storage and appended header contract](../page-source-persistence-and-csv.md),
[the shared parser/default contract](../page-directive-base-type-projection.md)
and [the reusable acquisition boundary](../physical-aspx-source-acquisition.md).

## What the offline integrated tests exercise

The fixtures start from synthetic raw discovery records and acquisition inputs,
not already-projected result-table rows. They invoke `AssessmentWebDiscovery`,
the real shared adapter/parser, `AssessmentDiscoveryWriter`, new native contexts
after closing disk handles, artifact retrieval and
`ReportManager.ExportClassicReportDataAsync`.

- `PageInheritsIntegratedTests` uses the real SDK factory/authentication/header
  pipeline ending in `DiscoveryTransportFixture`, with an exact request ledger.
  Its twelve success/configuration cases cover system/Form/View files with and
  without items, UTF-8 with/without BOM, UTF-16 LE/BE, raw/text hash differences,
  precedence, configuration uncertainty/conflicts and family/Handler separation.
  Twenty-six stream/failure/control cases independently check transport, capture,
  decoding, payload and parse states. Additional cases check rename/version
  history, replacement at one URL, retained later failures, missing identity/path
  and eight scheduled-scope/identity/path rejections (including sibling-prefix
  and encoded-traversal aliases) with zero unauthorized downloads. Both discovery
  and returned source paths must remain inside the recorded scheduled Web path.
- `AssessmentPageMetadataReplayTests` adds eight integrated system/Form/View/
  other-file item-admission replays using `ScanContextFixture` and the real
  native routing boundary, plus opposite source/item failure outcomes. Reopened
  source evidence must survive later body/profile work and independent CSV.
- `PageInheritsIntegratedRollbackTests` starts with discovery/acquisition,
  compares the complete native CSV, takes a consistent WAL-mode SQLite backup,
  executes Down/Up, restores, reopens and independently checks bytes, configuration
  and every exported field.

Expectations are hand-listed; SHA256 is independently recomputed from retrieved
bytes and CSV is parsed with an independent character reader. Existing
family/routing/accounting, report, upgrade/restart and acquisition/parser fixtures
remain in the full Release suite. All new source fixtures have
`Category=PageInherits`. Exact Linux prerequisite setup and commands are in
[reproducing the offline tests](../contributing/page-source-offline-tests.md).

## Backup, downgrade and source rollback

The appended migration adds only `ClassicPageDiscoveries.SourceEvidenceJson`,
`Scans.PageSourceEvidenceVersion` (default 0) and nullable
`Scans.PageBaseTypeConfigurationJson`. Historical migrations are not regenerated.
Use the existing temporary local `dotnet-ef` 8.0.3 convention, Core project,
Process startup project and `Storage/DatabaseMigration` output; do not install a
global tool. Review generated operations and SQLite SQL.

Back up schema/history, frozen configuration and embedded source bytes together.
Use SQLite's consistent backup API or stop writers, checkpoint/close WAL and
verify a closed copy. Copying only an active WAL database's main file is unsafe.
There are no raw-byte sidecars in this storage version.

Down to `20260928091529_ClassicPublishingLayoutTypeEvidence` removes exactly the
three new columns. It loses **all embedded original/partial bytes, discovery/read
observations, source identities/version/time, raw digests/encoding, declaration/
default/parse/configuration/Handler-availability evidence and scan source version/
configuration**. Inherited discovery/family/text-hash/routing/reference columns
remain. Up after Down creates null/uncollected fields and version 0; it is not
restore. Restore a verified pre-downgrade backup with matching code, then reopen,
retrieve bytes, recompute digests and compare native CSV.

Source rollback must first unwind future dependent consumers. Revert the source
delta in reverse dependency order in an **isolated work-repository copy**, check
the resulting tree against the declared input baseline, and rebuild/retest the
full original Release solution there. Never reset shared/checkpoint refs or
modify the read-only dependency to perform this exercise. Keep actual baseline/
final/revert pins and command evidence in a private delivery area.

Offline success makes no live/full-tenant support claim. Any future live claim
requires separate bounded authorization and readback. If the original controlled
test cohort is used, retain its `User-Agent: testtraffic-smr`. These fixtures do
not authorize tenant requests, production/server diagnostic jobs, expanded scope,
remote push, a pull request, release or publication.
