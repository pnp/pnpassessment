# aspx.csv file details

## Physical inventory, not Pages admission

`aspx.csv` exports the assessment's SQL `SPFile` facts in native `(SiteCollectionId, FileUniqueId)` order. Its only selection is a physical `.aspx` extension in the stored filename or path. It does **not** filter on NoExecute, handler, experience, product family, official status, catalog/list role, backing-item availability, or acquisition success.

Blocked, modern, catalog, registered view/form, denied, incomplete and unknown ASPX observations remain visible. Non-ASPX files, including `.pointpub`, are not part of this file. This is the observed inventory, not a claim that inaccessible scopes or files were successfully enumerated. Consult [discovery.csv](csv-discovery.md) for coverage.

## Columns and evidence

Every stored physical-file property is retained: assessment/native site, Web, list, folder, file and backing-item IDs; original server-relative path and filename; length, content type, customization and setup-path values; read/coverage/error states; observation timestamps; typed item-field states; original NoExecute text/source/read state; provenance JSON; and original `AspxEvidenceJson`.

The following flattened columns expose that recorded evidence without changing the source:

Column | Meaning
-------|--------
NoExecute | `true` for trusted server `1`, `false` for trusted `0`, empty for unavailable, invalid or conflicting evidence. Null is not replaced with false.
PageHandler | Exact fully qualified handler parsed from captured original bytes, or a previously captured handler when bytes are unavailable. No content-type or short-name fallback.
SourceSha256 | Digest of captured source bytes, when available.
SourceReadState / SourceError | Original source acquisition outcome and error.
HandlerResolution / PageDeclaration | Source parser outcome and original Page declaration.
ListTemplate | Observed owning-list template, when available.
IsCatalog / IsListView / IsListForm | Independent captured native role evidence; unavailable values stay empty.
RoleReadState | Completeness of role acquisition, independent of source and item acquisition.
Official / OfficialReadState | No separate authoritative official-file observation is currently acquired: the value stays empty with `NotAcquired`. SetupPath, catalog role or an unfamiliar handler is not substituted for this evidence.

`AspxEvidenceJson` retains the recorded source evidence, including original bytes when acquired. Treat exported source and error details as assessment data; synthetic examples in this repository contain no real tenant data.

Nullable text uses the existing `BackslashNullV1` encoding: after normal CSV unquoting, `\N` is null, an empty field is a known empty string, and a leading backslash in a real value is escaped with one extra backslash. Nullable Boolean, integer and GUID fields use empty values.

`aspxsummary.json` counts the rows actually written and their true/false/unknown NoExecute distribution. It does not substitute the selected Pages count or assert tenant completeness.

## Other outputs and Power BI

[classicpages.csv](csv-classicpages.md) is a separate, deliberately filtered union of qualifying physical ASPX Pages and independent Posts items. The older [classicaspx.csv](csv-classicaspx.md) remains a legacy business projection for existing consumers; it is **not** the unfiltered inventory and must not be used as the new ASPX tab's source.

The intended Power BI ASPX tab must faithfully show all rows from `aspx.csv` before an explicit user-selected visual filter. The matching new PBIT/layout has not been promoted in this revision: the user's authoritative unsaved layout was unavailable. Existing PBIT assets are unchanged rather than replaced with a guessed layout.
