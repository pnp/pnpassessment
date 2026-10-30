# classicaspx.csv file details

## Summary

This csv file is the Classic ASPX business projection of the raw physical files stored for an assessment. It is not a dump of the SPFile SQL table. The table keeps observed file facts independently of file type; this export selects physical ASPX files and excludes files identified as modern pages by the existing Classic assessment heuristics.

One row represents one real SiteCollectionId and FileUniqueId pair, not a list-item address. Stock forms/views and unclassified ASPX files remain visible. Missing or failed metadata is not evidence that a file is modern or absent. Legacy item-only Blog/CP4 assessment remains in [classicpages.csv](csv-classicpages.md); it does not multiply physical-file rows.

With `--homepageonly`, the input facts are limited to observed welcome-page targets. Use [discovery.csv](csv-discovery.md) for scopes, references, pagination, gaps and scan-wide coverage. The accompanying `classicaspxsummary.json` counts selected business rows and unknown/poison observations, records scope and CSV hash, and does not assert tenant completeness. An empty result still exports its typed header.

## Columns

The following columns are included:

Column|Description
------|-----------
ScanId | Id of the assessment; ordinary scan ownership, not part of the physical file key.
SiteCollectionId | Real site collection id. Together with FileUniqueId this identifies the physical file.
WebId | Observed web id, when available.
ListId | Observed list or library id, when available.
FileUniqueId | Real SPFile.UniqueId. No key is generated from a URL.
SiteUrl | Fully qualified site collection URL.
WebUrl | Fully qualified web context URL associated with the observation.
Url | Server-relative file URL encoded from the stored decoded ResourcePath. Literal percent sequences, spaces and hash names remain distinct; the original SQL path is not changed.
FileName | Leaf name of the physical file.
ClassicPageType | Result of the existing item-field heuristics or existing content-type fallback. ModernPage and ModernSitePage results are excluded. This PR does not introduce a new classification algorithm or precedence.
PageRole | Existing SetupPathClassifier template-family result, such as ListView, ListForm, WebPartPage or Unknown. Existing blank-setup fallback remains a heuristic, not proof that unavailable metadata establishes an unghosted file.
ClassificationState | ItemMetadata when existing heuristics use restored item inputs, FileOnlyFallback when only existing content-type inference is available, or Unknown when no result can be inferred.
ClassificationReason | Identifies the existing heuristic or fallback used; unavailable item-field snapshots are explicit.
ContentTypeId | Content type id observed in the raw metadata, when available.
CustomizedPageStatus | Observed SharePoint customization status: Uncustomized, Customized or None. Unavailable values do not prove ghosting.
SetupPath | Observed vti_setuppath, when available. Absence of an observation is not proof of user-authored content.
NoExecuteRaw | Original string returned for the requested SPO NoExecute field, preserved without trimming or converting it to a boolean. This is a server-projected one-bit field, not the complete DocFlags bitmask.
IsPoisoned | True only for an available, trusted original NoExecute value exactly `1`; False for exactly `0`; empty when unavailable, invalid or conflicting. Calculated during export, not persisted as a file fact.
PoisonState | Poisoned, NotPoisoned or Unknown, based only on the captured server NoExecute field and its source/read/conflict evidence.
RenderDisposition | NoExecuteDownload when the field blocks execution; NotBlockedByNoExecute when it does not; Unknown otherwise. NotBlockedByNoExecute does not guarantee that an actual HTTP request will render: other policies and permissions still apply.
MetadataReadState | Read outcome for the selected item metadata used by classification. Successful physical discovery does not imply these fields were available.
ErrorCodes | Retained file/item observation error codes, when present.
ErrorDetail | Retained details of the observation errors, when present.
ObservedAtUtc | Latest retained file observation time, in UTC.

## NoExecute evidence

The current implementation requests NoExecute with the existing selected-page list-stream metadata query. It does not execute or download the ASPX to test rendering. Missing fields, explicit null, invalid values and failed reads are distinct in SQL; none is replaced with `0`. Where no backing item or readable field is available, poison remains Unknown. The summary declares `poisonEvidenceKind=ServerNoExecuteField` and `fullDocFlagsCollected=false`.

## Nullable text values

Nullable text uses BackslashNullV1 after CSV quoting is parsed:

CSV field value | Meaning
----------------|--------
`\N` | SQL NULL: unavailable or unknown.
Empty field | A known empty string, not SQL NULL.
`\\N` | Literal text `\N`. For a non-null field beginning with two backslashes, remove exactly one escape backslash.

This applies to SiteUrl, WebUrl, Url, FileName, ClassicPageType, ContentTypeId, CustomizedPageStatus, SetupPath, NoExecuteRaw, ErrorCodes and ErrorDetail. Nullable GUIDs and IsPoisoned use empty fields when unknown. Other assessment CSV encodings are unchanged.
