# aspxinventory.csv file details

## Summary

This csv file contains one row for each unique physical ASPX file observed within the requested Classic Pages scope. Files are identified by their real site collection and file GUIDs, not by their URL or by a list item. Repeated observations of the same file are merged.

With `--homepageonly`, the file contains only observed welcome-page targets. A row does not establish that the surrounding folder, web or tenant was completely inspected. Use [discovery.csv](csv-discovery.md) for scope, reference, pagination and gap evidence, and [classicpages.csv](csv-classicpages.md) for Classic page assessment results, including legacy item-only blog and display-form pages.

The accompanying `aspxsummary.json` records the observed row count, declared scope, coverage-state counts, nullable-text encoding and CSV file hash. An empty inventory still exports its column header; a zero count is not proof of complete absence.

## Columns

The following columns are included:

Column|Description
------|-----------
ScanId | Id of the assessment.
AspxKey | Stable physical-file key in the form `tenant:v1:{SiteCollectionId:N}:{FileUniqueId:N}`. The `N` format uses 32 hexadecimal characters without hyphens. Together with `ScanId`, this identifies one inventory row.
SiteCollectionId | Real id of the site collection containing the observed file.
WebId | Observed web id, when available. It is supplementary metadata and is not part of the file identity.
ListId | Observed list or library id, when available. A missing value does not prevent a physical file from being inventoried.
FolderUniqueId | Observed GUID of the folder containing the file, when available.
FileUniqueId | Real SharePoint file GUID (`SPFile.UniqueId`). No GUID is generated from a URL.
SiteUrl | Fully qualified site collection URL.
WebUrl | Fully qualified URL of the web context in which the file was observed.
Url | Fully qualified file URL. Decoded physical path segments are URL-encoded, preserving literal percent sequences, spaces and hash characters as distinct file names.
FileName | Leaf name of the physical file.
ContentTypeId | Content type id observed in the file metadata, when available.
CustomizedPageStatus | Observed SharePoint customization status: `Uncustomized` (ghosted), `Customized` (unghosted), or `None` (no customization classification). An unavailable value is not evidence of ghosting.
SetupPath | Observed `vti_setuppath` template path, when available. An unavailable value does not by itself establish that the file is unghosted or user authored.
FileReadState | State of the physical-file metadata observation, normally `Complete` for an admitted folder-file observation. This is not a source-content read or a modernization-assessment result.
FileErrorCodes | Retained error codes associated with the file observation, when present.
FileErrorDetail | Retained details associated with the file observation errors, when present.
ProvenanceJson | JSON source evidence merged across observations, such as raw discovery record keys, observation methods and retained metadata conflicts.
CoverageState | Coverage state of this file observation, normally `Observed`; uncertainty or conflicting observations can retain states such as `Unknown` or `ChangedDuringScan`. This is not the scan-wide coverage verdict.
FirstObservedAtUtc | Earliest retained observation time for this file in the assessment, in UTC.
ObservedAtUtc | Latest retained observation time for this file in the assessment, in UTC.

## Nullable text values

Nullable text columns use `BackslashNullV1` to preserve the difference between an unknown value, empty text and literal backslashes. Parse CSV quoting first, then decode the field:

CSV field value | Meaning
----------------|--------
`N` | SQL NULL: the value is unavailable or unknown.
Empty field | A known empty string, not SQL NULL.
`\N` | Literal text `N`. More generally, remove exactly one backslash from a non-null field that begins with two backslashes.

This encoding applies only to the nullable text columns: `SiteUrl`, `WebUrl`, `Url`, `FileName`, `ContentTypeId`, `CustomizedPageStatus`, `SetupPath`, `FileErrorCodes` and `FileErrorDetail`. Optional GUID columns use empty fields when unavailable. It does not change the encoding of other assessment CSV files.
