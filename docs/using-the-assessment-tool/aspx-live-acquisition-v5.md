# ASPX live acquisition v5 tenant authority

## Product tenant authority mode

`aspx-acquisition --scope-mode product_tenant_authority` freezes the complete product authority snapshot before page acquisition starts. The command does not accept `--site` in this mode.

The snapshot uses the existing Assessment/PnP Core Admin paths:

- site collections: `ISiteCollectionManager.GetSiteCollectionsAsync(filter: SiteCollectionFilter.Default)`;
- root web identity: `IWeb.GetAsync(Id, Url, ServerRelativeUrl, WebTemplateConfiguration)`;
- subwebs: `ISiteCollectionManager.GetSiteCollectionWebsWithDetailsAsync(siteUrl, skipAppWebs: false)`.

`SiteCollectionFilter.Default` is recorded as the actual filter. Personal sites and app webs are not silently excluded by the Assessment caller. The fully materialized site and web results, provider/API names, filters, direct parent linkage, exclusions, failures and continuation state are canonicalized as `aspx-tenant-authority/v1`. Its SHA-256 becomes both `ScopePolicyHash` and `TenantManifestHash` in the resolved physical manifest and is copied into the reference/aggregate bindings.

`TenantVisibilityVerified=true` is permitted only when the product mode is active, site enumeration is terminal, every enumerated site has a terminal root/subweb result, no result has an exclusion or unconsumed continuation, and no authority failure remains. A returned root web remains observable when subweb enumeration is denied, but the per-site `ProductRootAndSubwebAuthority` receipt remains `Denied` or `Failed`; it is never converted to empty.

## Declared subset mode

`--scope-mode declared_subset --site <url> [...]` remains available for bounded runs. The product subweb adapter is still used for each declared site, but the site denominator records `tenant-site-denominator-not-enumerated`. Declared mode can never set tenant visibility true or produce a tenant-complete verdict.

The old `--authority-revision` and `--authority-hash` switches are retained only as deprecated CLI compatibility inputs. They do not replace the product-generated authority snapshot.

## WelcomePage and Web-root modeled adapters

WelcomePage acquisition uses `PnP.Core.Model.SharePoint:IWeb.GetAsync(WelcomePage)`. Recursive Web-root folder/file acquisition uses `IWeb.GetFolderByServerRelativeUrlAsync` with modeled `Folders` and `Files` properties. These adapters have separate denominator rows and record the modeled provider and operation.

A modeled denial or failure remains an explicit terminal result with `expectedCountState=Unknown` and `expectedCount=null`. The command does not fall back to a false empty result. Document-library folder/file enumeration continues to use the paginated live collection adapter where that authority is supported.

## Surface applicability matrix

Every observed list records actual `BaseType`, `BaseTemplate`, `Hidden`, `IsCatalog` and a surface-family classification. Separate applicability rows cover:

- document-library files;
- physical list `Forms` trees;
- list Forms objects;
- list Views objects;
- page libraries and catalog/page-layout libraries through their actual list metadata;
- Web-root files and WelcomePage through their modeled adapters;
- setup/virtual references through the independently reviewed platform registry.

Non-document lists use `SystemOrVirtualOnly` for list-root file applicability; their physical `Forms` tree is still acquired as an independent physical surface. Missing BaseType/root-folder facts remain `Unknown`. A current failure is not sufficient to create a `NotApplicable` disposition.

## Resume and completeness

The authority hash includes the scope mode, tenant root, actual provider/operation/filter, observed site/web identities, direct parent links, exclusions, failure state and continuation state. A product-vs-declared change, site/web denominator change, permission-boundary change, or authority outcome change therefore changes immutable provenance and causes existing physical/reference resume validation to fail closed.

The existing physical/reference/aggregate and terminal versions remain byte-bound by the v2/v1 contracts described in `aspx-live-acquisition-v4.md`. Any unresolved `Denied`, `Failed`, `Unknown`, continuation, applicability gap, or `changed_during_scan` state prevents an aggregate PASS.

## Examples

Tenant authority:

```text
microsoft365-assessment aspx-acquisition \
  --scope-mode product_tenant_authority \
  --tenant https://contoso.sharepoint.com \
  ...
```

Bounded declared subset:

```text
microsoft365-assessment aspx-acquisition \
  --scope-mode declared_subset \
  --site https://contoso.sharepoint.com/sites/a \
  --tenant https://contoso.sharepoint.com \
  ...
```

All output paths must be new for a first run. Resume requires the exact run ID and unchanged resolved manifest, scope mode, authority hash, provider, registry, build, permission boundary and snapshot fence.
