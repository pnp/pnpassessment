# Physical ASPX source acquisition

Classic assessment reuses its existing discovery entry point, authentication,
scheduled Web context and PnP file-download capability. It does not navigate to an
ASPX URL as a rendered page, create another scanner, or require a list item to
read a discovered physical file.

`AssessmentWebDiscovery` commits discovered existence before the acquisition
callback. `AspxSourceAcquisition` returns each reusable `AspxSourceReadResult` to
that callback's row and adapts reliable results to the inherited
`PublishingLayoutTypeEvidence` API. The old decoded-text API is still available.
This boundary is in memory: `DiscoveryObservation` and `SourceReads` are
explicitly excluded from EF and CSV. Durable byte/observation storage and new
export columns require a separate persistence consumer and matching migrations
for any schema change. This implementation does not claim byte retrieval after
reopening an assessment database.

## Identity and observations

A resolved physical identity consists of Site collection ID, Web ID and File
UniqueId. URLs and names may change; list/item identity is optional. A URL is not
a unique physical identity. Unresolved discovery rows have observation keys,
not manufactured File UniqueIds. Original discovery values, including invalid
or missing raw identity strings, remain in a copied discovery snapshot.

Source identity and discovery identity are separate. The PnP adapter checks the
scheduled Site/Web and rejects a changed File UniqueId before download. A
renamed file with the same tuple can retain its original discovery name/path
and its newly observed source name/path. Available list/item metadata is retained
without accessing `ListItemAllFields` or making item admission a prerequisite.

ETag and major/minor version metadata are recorded when returned. Otherwise the
result explicitly says `ObservationTimeOnly` and retains source-observation UTC
time. Metadata and content are not asserted to be an atomic server snapshot.
Requested but unreturned value-type properties are not interpreted as zero
lengths, zero versions or observed GUIDs.

## Bytes, decoding and independent outcomes

The reader captures the original stream before decoding. It retains exact bytes,
including BOMs, actual captured length and a SHA256 digest. Consumers receive
defensive byte copies. Null capture means bytes were not returned/captured;
an observed zero-byte response has an actual length of zero and an empty-byte
digest. Unknown expected file length remains null.

Strict UTF-8 (with a documented BOM-less assumption), UTF-16 LE/BE and UTF-32
LE/BE decoders reject malformed sequences. BOM selection and decoding reasons
are retained. The reader does not guess a legacy code page or silently replace
invalid bytes. Suspicious NUL-containing text is not considered reliably decoded
ASPX source.

Transport outcome, capture completeness, decoding and payload classification
are independently available. Examples include:

| Observation | Transport | Capture | Payload |
| --- | --- | --- | --- |
| Valid physical source, normal EOF | Complete | Complete | Source |
| Returned HTTP 401/403 error body | Denied | Complete or Partial | Independently inspected |
| Login/denial shell over successful transport | Complete | Complete | LoginShell or SemanticDenied |
| Rendered HTML without a source preamble | Complete | Complete | RenderedHtml |
| No returned stream | NotReturned | NotCaptured | NotInspected |
| Missing path/out-of-scope identity | NotAttempted | NotCaptured | NotInspected |
| Stream interruption/length mismatch | Partial | Partial | Available prefix independently inspected |
| Malformed encoding | Complete or Partial | Complete or Partial | Unknown |
| Observed zero-byte response | Complete | Complete | Empty |

The existing semantic-denial detector is reused after strict decoding, including
UTF-16 shells. A leading server-source preamble is distinguished from
directive-looking strings in rendered HTML. This is a conservative acquisition
check, not a Page directive parser, deployment-configuration engine or CLR
registry.

Capture is bounded to 16 MiB by default. At that limit the reader conservatively
reports partial capture if EOF was not observed. A partial digest is labeled
`PartialCapturedBytes`, never a whole-file digest. Valid text inside a partial
capture is retained, but cannot establish family membership or justify inferred
defaults. Cancellation propagates rather than becoming a successful or failed
observation. The inherited PnP download API lacks a per-call cancellation token;
the adapter checks cancellation before/after that call and during stream reads.

### Raw digest versus legacy SourceHash

The raw digest hashes the captured bytes exactly. The inherited `SourceHash`
hashes UTF-8 encoding of decoded text and is labeled `DecodedTextUtf8Sha256`.
Equal decoded text in different encodings can share that legacy hash while
having different raw digests. Neither hash is an observed request Handler or
evidence of server-side type execution.

## Compatibility and failure isolation

The legacy family predicate still requires reliable source and conservative
family resolution. No declarations/defaults are inferred from denial, unknown
payloads, decode failure, empty responses or prefixes. A no-Inherits directive
retains the inherited `Unknown`/`InheritsMissing` family behavior; declared and
default base-type projection is a separate parser consumer.

A source failure adds a per-file observation, does not erase another successful
facet, does not change successful file enumeration into a whole-page skip, and
does not prevent unrelated discovered files from being processed. Database
write failures still fail the worker rather than pretending acquisition succeeded.
Historical scans do not gain family-rule authority merely because acquisition
is available.

## Offline verification and local tooling

The synthetic fixtures carry `Category=PageInherits`. From `src/PnP.Scanning`,
after preparing the authorized local toolchain and read-only dependency/output
mapping, run:

```text
dotnet build -c Release
dotnet test -c Release --logger 'trx;LogFileName=source-acquisition-release.trx'
dotnet test PnP.Scanning.Core.Tests/PnP.Scanning.Core.Tests.csproj -c Release --filter 'Category=PageInherits' --logger 'trx;LogFileName=source-acquisition-focused.trx'
```

On Linux, keep the supplied SDK unchanged and provide matching official .NET 8
and ASP.NET Core 8 shared runtimes in a local tool directory. Verify published
archive SHA512 before extraction. Use a local host with read-only SDK/reference
pack mappings, not image mutation or major roll-forward. Keep temporary/CLI/
package caches and every project's intermediate, generated, documentation and
build output outside the read-only dependency. The operational preparation
recipe, exact commands, pinned versions, hashes, logs and runtime-host traces
belong in the private delivery area, not the public repository.

All examples and test artifacts here are synthetic. Offline results do not
assert live or full-tenant support. Real tenant artifacts, runtime archives,
credentials, operational binding receipts and private planning information
must stay outside Git. This change does not authorize tenant/server jobs, remote
publication or additional acquisition scope.
