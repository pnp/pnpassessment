# Page declarations and default base types

This is a source-evidence contract for Classic assessment, not an ASP.NET
compiler, deployment-configuration reader, CLR registry or request Handler
observation. It reuses the [physical ASPX acquisition boundary](physical-aspx-source-acquisition.md).
All fixtures and examples are synthetic and offline.

## Shared parser and retained values

`PageDirectiveParser` is the single reader used by `PageBaseTypeProjection` and
the compatible `PublishingLayoutTypeEvidence` wrappers. Only a real `Page`
directive in the leading source preamble can declare the physical file's type.
Whitespace, a leading BOM, server/HTML comments and other directives can precede
it. Directive and attribute names are case-insensitive. Attribute order,
multiline whitespace and single/double quotes are supported. A bare custom
**type name** is supported as a quoted attribute value; unquoted attributes are
malformed.

`DeclaredInherits` retains the exact text between the attribute quotes: no
trimming, entity decoding, comment stripping or assembly-name rewriting. This
includes significant spaces and line breaks. `NormalizedInherits` is separate
and trims only surrounding whitespace. The projected `BaseType` uses that
minimal normalization, not the family binder's canonical CLR identity.

The parser retains directive names, raw text, offsets in the supplied decoded
text (not byte offsets; acquisition consumes a recognized BOM), preamble
placement, termination/truncation flags and ordered attribute evidence. Equal
duplicate values are still duplicates; attribute names differing only by case
are duplicates too. No duplicate directive/attribute is selected by order.
Malformed directives retain usable complete values or unclosed value prefixes
without becoming a successful type projection.

The remainder is scanned for actual late/duplicate directives, not searched
with a tail regex. Body literals, quoted markup attributes, server blocks,
server/HTML comments and script/style/textarea/xmp bodies cannot supply a Page
declaration or a false duplicate. A real late Page directive is misplaced; a
second actual Page directive is ambiguous, even after markup. Structural
uncertainty is rejected conservatively. This lexical boundary does not
interpret arbitrary embedded programming languages or prove that ASP.NET can
compile the page.

Inspection is bounded to 16 MiB of decoded characters, 64 KiB per directive,
128 directives and 256 attributes per directive. Limit results retain bounded
evidence and suppress inference, rather than treating an uninspected suffix as
absent.

## Independent source, parse and projection states

| Parse status | Meaning |
| --- | --- |
| `Declared` | One syntactically valid leading Page with nonblank Inherits |
| `VerifiedAbsent` | The inspected text has one valid leading Page without Inherits, and no ambiguous/uncertain suffix |
| `EmptyInherits` | An explicit empty/whitespace value, retained exactly; not absence |
| `PageDirectiveMissing` / `MisplacedPageDirective` | No applicable leading Page; no default |
| `DuplicateAttribute` / `DuplicatePageDirective` | Ambiguous input, including equal duplicate values; no default |
| `MalformedDirective` / `LexicallyUncertain` | Invalid or uncertain syntax; no default |
| `SourceUnavailable` / `EmptySource` | No decoded source or observed empty text, respectively; not absence |
| `InspectionLimitExceeded` | Inspection was incomplete by policy; no default |

These are **lexical states of the supplied text**, not acquisition outcomes.
Even a complete-looking directive in a partial/denied capture can have retained
lexical evidence. Whole-physical-source absence requires
`PageBaseTypeProjection.IsVerifiedAbsence`, which combines the absence parse
with `AspxSourceReadResult.IsReliableSource`. `IsReliableParse` likewise never
becomes true for an untrusted capture.

Transport, capture, content classification, decoding and physical identity
remain available unchanged on `SourceRead`. Denied, Failed, not-returned,
not-attempted, Unknown content, login/rendered HTML, decode failure, empty
source, identity uncertainty/conflicts and partial/truncated source cannot
produce either inferred default or a verified declaration projection. Unknown
lengths remain null; observed empty responses retain their actual zero length.
Usable declaration fragments and original bytes remain evidence, but `BaseType`
is null and `TypeSource` is `Unknown` in these cases.

## Projection precedence

For reliable physical-source acquisition:

1. A valid explicit nonblank Inherits produces `TypeSource=Declared` and its
   minimally normalized `BaseType`, irrespective of CLR-family resolution or
   configuration uncertainty.
2. Only reliable absence can use applicable, supplied effective
   `pages.pageBaseType` evidence: `TypeSource=ConfiguredDefault`.
3. With reliable absence and no known applicable override, `DeclaredInherits`
   is null, `BaseType=System.Web.UI.Page` and `TypeSource=FrameworkDefault`.
   `IsFrameworkDefaultAssumption` and the reason explicitly identify this as
   a framework-default **assumption**.

Unknown deployment configuration stays `Unknown`, not verified no-override.
Out-of-scope evidence remains retained and leaves the current file's
configuration knowledge Unknown. Known applicable conflicting/unsupported
evidence suppresses defaults; it is not silently replaced by a verified
override or a framework guess. `NotDeclared` is not used as a page type or
sentinel `BaseType`.

## Minimal supplied configuration contract

`PageBaseTypeConfiguration.Capture(IConfiguration)` uses the assessment's
existing configuration mechanism. It captures values only; it does not read
web.config, issue tenant requests, evaluate inheritance/location rules or
schedule a configuration-fetch job.

The following fragment illustrates the supplied evidence section:

```json
{
  "PageInherits": {
    "PagesPageBaseTypeEvidence": [
      {
        "KnowledgeState": "EffectiveOverride",
        "EffectivePageBaseType": "Synthetic.ConfiguredPage",
        "Provenance": "Synthetic frozen effective web.config evidence, revision 1",
        "Reason": "Synthetic file applicability confirmed",
        "FileScope": {
          "SiteCollectionId": "11111111-1111-1111-1111-111111111111",
          "WebId": "22222222-2222-2222-2222-222222222222",
          "FileUniqueId": "33333333-3333-3333-3333-333333333333",
          "ServerRelativePath": "/sites/synthetic/default.aspx"
        }
      }
    ]
  }
}
```

Only exact Site/Web/File IDs **and** an exact server-relative ASPX path are
supported. No wildcard, directory, parent-path or configuration hierarchy
scope is inferred. Provenance is mandatory for an effective override or
supplied `VerifiedNoOverride`. Those knowledge states are supplied/frozen
evidence assertions, not runtime attestation performed by this tool.
`VerifiedNoOverride` requires a null effective value; null, empty and blank
override values are not interchangeable. Other knowledge states are `Unknown`,
`Conflicting` and `Unsupported`. Unrecognized states, inconsistent values,
missing scope/provenance and overlapping applicable records are unsupported
or conflicting, not selected by file order. All input strings are retained.

Snapshots are immutable and support versioned `ToJson`/`FromJson` round trips.
Unsupported versions, malformed/oversized frozen JSON and entry-count limits
remain explicit unsupported evidence. Scan initialization/persistence must
freeze the captured snapshot and restore it without rereading mutable current
settings on restart. An absent snapshot means Unknown configuration.

## Integration, family and compatibility

`AspxSourceAcquisition.ForScan` accepts an optional frozen configuration
snapshot. Under the existing current scan authority it records an in-memory
projection before list-item admission and passes the **same parse result** to
the family adapter. No snapshot means Unknown configuration. Historical
version-zero callbacks acquire source without inventing projection authority.

This change supplies the parser/configuration/projection consumer boundary.
`ClassicPageDiscovery.PageBaseTypeProjections`, like `SourceReads`, is
explicitly excluded from EF and CSV. It does not introduce an unmigrated schema
or automatically enable durable configuration/default fields in the CLI.
Scan initialization, durable configuration/source mapping, database reopening
and new CSV columns are the persistence consumer's responsibility. Schema
changes at that boundary require matching migrations; no historical migration
is changed here.

The old decoded-text family API remains available, with its legacy decoded-text
UTF-8 `SourceHash` and inherited reasons. New optional parse-status/reason
fields in observation JSON do not backfill old observations. The
`PublishingLayoutTypeCatalog` binder, anchors, rule version, ancestry and
conservative merge/predicate are unchanged.

A Page without Inherits can have a successful `FrameworkDefault` projection
while retaining `PublishingLayoutFamily=Unknown` and `InheritsMissing`.
Even a configured default equal to the PublishingLayoutPage root cannot prove
family membership. Bare names and `CodeFile`/`Src` dynamic compilation can have
`Declared` projection while family resolution remains Unknown. Defaults never
populate `DeclaredPageType`, `ResolvedPageType`, ancestry or the family
predicate. They are not copied into an observed Handler field. No server
diagnostics are performed; Handler evidence remains server-only/unavailable.

## Offline tests and rollback

From `src/PnP.Scanning`, with an authorized local runtime/dependency/output
setup:

```text
dotnet build -c Release
dotnet test -c Release --logger 'trx;LogFileName=cp1-release.trx'
dotnet test PnP.Scanning.Core.Tests/PnP.Scanning.Core.Tests.csproj -c Release --filter 'Category=PageInherits' --logger 'trx;LogFileName=cp1-fixtures.trx'
dotnet test PnP.Scanning.Core.Tests/PnP.Scanning.Core.Tests.csproj -c Release --filter 'FullyQualifiedName~PublishingLayoutTypeEvidenceTests' --logger 'trx;LogFileName=family-regressions.trx'
```

`PageDirectiveParserTests`, `PageBaseTypeProjectionTests`,
`PageBaseTypeAcquisitionTests` and `PageBaseTypeBoundaryTests` cover hand-listed
synthetic outcomes. Existing acquisition, discovery, family, routing,
persistence and upgrade tests remain intact. On Linux, use matching local
.NET/ASP.NET Core 8 shared runtimes without changing the supplied SDK, target
frameworks, image or read-only dependency. Exact preparation, output mapping,
tested commit, logs, timestamps, exit codes and TRX belong in the operational
delivery area, not Git.

This in-memory change needs no schema downgrade. Reverting it restores the
inherited reader/adapter, but future persistence or other dependent consumers
must first unwind their own dependent changes. Offline verification does not
claim live/full-tenant support or authorize tenant/server jobs, remote
publication, a push or a pull request.
