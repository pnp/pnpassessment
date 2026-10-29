# Classic ASPX folder inventory

The Classic Pages scan records real ASPX files observed through SharePoint folders. List items, content types and form declarations cannot create inventory resources.

## Physical authority

- Site collection and Web enumeration define declared scan scope.
- Web root folders, all authorized list root folders and their subfolders feed the existing folder discovery pipeline.
- Files/Folders pagination, denied/truncated/cancelled evidence remain explicit; failure is not an empty folder.
- Hidden flags, list base types and page families do not filter physical file admission.
- Repeated folder entry points reuse only complete physical content while retaining each parent scope.
- Stable identity is real SiteCollectionId plus File.UniqueId; URLs are locators, not generated GUIDs.
- HOME remains targeted and never claims complete tenant coverage.

This covers content-store files, not virtual /_layouts application endpoints or seeded platform catalogs.

## Metadata and Classic assessment

List/default-display-form/content-type/form/view declarations are role evidence, not physical file or Item counts. Existing discovery.csv Reference observations retain these declarations; a URL cannot create a missing inventory file. Existing Classic page assessment, rich shared metadata and report denominators remain separate.

Inventory does not enumerate list items, fetch source bytes, infer runtime Handlers or create Item-to-ASPX bindings.

## Running

Select the existing Classic Pages component. Existing tenant/auth/site-list/HOME flags are unchanged. --excludesitetemplates defaults to SPSPERS#*,APP#*; explicit patterns replace defaults and sole none clears them. Each Web is evaluated independently. There is no platform-catalog input for folder inventory.

## Outputs

- aspxinventory.csv: one normalized physical file identity per ScanId.
- aspxsummary.json: observed count, declared scope, coverage, nullable encoding and CSV SHA256/length.
- Existing discovery.csv: physical/scope/pagination/gap and metadata-reference evidence.

There is no binding CSV/count, compatibility alias or inventory-only Info file. A fresh empty inventory still has a typed CSV header. Observed zero never proves absence outside successfully enumerated folders.

### Inventory columns

| Purpose | Columns |
|---|---|
| Stable identity | ScanId, AspxKey, SiteCollectionId, FileUniqueId |
| Supplementary observed scope | WebId, ListId, FolderUniqueId |
| Location | SiteUrl, WebUrl, Url, FileName |
| Optional observed metadata | ContentTypeId, CustomizedPageStatus, SetupPath |
| File observation | FileReadState, FileErrorCodes, FileErrorDetail |
| Evidence and coverage | ProvenanceJson, CoverageState, FirstObservedAtUtc, ObservedAtUtc |

The entity and export share these 21 fields. Missing owner scope stays nullable; uniqueness never depends on observation Web/List or URL.

### Nullable text

Inventory nullable text uses BackslashNullV1 after CSV parsing: NULL is literal \N, empty text stays empty, and a non-null leading backslash is escaped with exactly one extra backslash. GUIDs/timestamps and existing Classic converters keep their contracts.

## Storage and migration

ClassicAspxInventories has primary key (ScanId,AspxKey) and unique physical identity (ScanId,SiteCollectionId,FileUniqueId). Writer batches are bounded, atomic and idempotent; metadata conflicts remain evidence, not invented certainty.

The unmerged inventory-only migration is generated from current main with matching EF tooling in Debug. Fresh databases and main-baseline upgrades are acceptance paths. Earlier local preview databases are not rolled back/deleted or silently upgraded by rewriting migration history; start a fresh scan for the reduced preview schema.
