# aspx.csv file details

## Physical ASPX inventory

`aspx.csv` exports the assessment's SQL `SPFile` facts in native `(SiteCollectionId, FileUniqueId)` order, with one row per observed native site/file pair. Selection uses the physical `.aspx` extension in the stored filename or path. Every matching observation is included across NoExecute values, handlers, experiences, product families, official states, catalog/list roles, backing-item availability and acquisition outcomes.

Blocked, modern, catalog, registered view/form, denied, incomplete and unknown ASPX observations remain visible. The population consists of observed physical ASPX files within the requested assessment scope. Consult [discovery.csv](csv-discovery.md) for scope, reference, pagination, gap and coverage evidence, including inaccessible scopes and files.

## Columns and evidence

Every stored physical-file property is retained: assessment/native site, Web, list, folder, file and backing-item IDs; original server-relative path and filename; length, content type, customization and setup-path values; read/coverage/error states; observation timestamps; typed item-field states; original NoExecute text/source/read state; provenance JSON; and original `AspxEvidenceJson`.

The following flattened columns expose the recorded evidence alongside its original source:

Column | Meaning
-------|--------
NoExecute | `true` for trusted server `1`, `false` for trusted `0`, empty for unavailable, invalid or conflicting evidence. Unknown values retain their nullable state.
PageHandler | Exact fully qualified handler parsed from captured original bytes, or a previously captured handler when bytes are unavailable. Handler identity comes from the recorded source evidence.
SourceSha256 | Digest of captured source bytes, when available.
SourceReadState / SourceError | Original source acquisition outcome and error.
HandlerResolution / PageDeclaration | Source parser outcome and original Page declaration.
ListTemplate | Observed owning-list template, when available.
IsCatalog / IsListView / IsListForm | Independent captured native role evidence; unavailable values stay empty.
RoleReadState | Completeness of role acquisition, independent of source and item acquisition.
Official / OfficialReadState | Authoritative official-file evidence has its own fields. The current acquisition state is `NotAcquired`, with an empty Official value. SetupPath, catalog role and handler identity retain their independent meanings.

`AspxEvidenceJson` retains the recorded source evidence, including original bytes when acquired. Treat exported source and error details as assessment data. Repository examples use synthetic data.

Captured bytes take precedence over older flattened handler/declaration values. Malformed or unresolved bytes produce an unknown handler, with `HandlerResolution`/`SourceError` exposing the parser result. The original acquisition can still be `Available` when byte reading succeeded and parsing failed. The raw row is retained in all of these cases.

Nullable text uses the existing `BackslashNullV1` encoding: after normal CSV unquoting, `\N` is null, an empty field is a known empty string, and a leading backslash in a real value is escaped with one extra backslash. Nullable Boolean, integer and GUID fields use empty values for unknown observations.

Within captured typed item-field JSON, an omitted optional user/lookup property represents an unacquired value; explicit JSON null and text remain distinct. Other returned item fields remain available alongside unavailable SDK display properties. Lookup IDs use observed native IDs or an unknown state; the SDK's `-1` sentinel maps to unknown. Other SDK errors retain their acquisition-failure state.

`aspxsummary.json` counts the rows actually written and their true/false/unknown NoExecute distribution. These counts describe the exported physical inventory. [discovery.csv](csv-discovery.md) supplies the assessment's coverage evidence.

## Other outputs and Power BI

[classicpages.csv](csv-classicpages.md) contains the selected union of qualifying physical ASPX Pages and independent Posts items. [discovery.csv](csv-discovery.md) records physical-file discovery, references, scopes, pagination, gaps and coverage.

The intended Power BI ASPX tab uses all rows from `aspx.csv` before an explicit user-selected visual filter. This revision retains the existing PBIT assets; the matching ASPX tab layout remains a separate report-template update.
