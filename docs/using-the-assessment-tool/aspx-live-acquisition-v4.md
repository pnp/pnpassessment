# ASPX live acquisition v4 contract hardening

## Scope

This revision hardens the Assessment `aspx-acquisition` product path after the bounded CCD-726/CCD-734/CCD-393 evidence. It does not add tenant/site authority enumeration, PnP ingredient logic, replay logic, or source/target mutation.

The physical volume remains `aspx-discovery-output/v2` and `aspx-discovery-sqlite/v2`. The companion contracts are versioned as follows:

| Contract | Version |
| --- | --- |
| surface denominator | `aspx-surface-applicability-denominator/v4` |
| pagination page receipt | `aspx-pagination-page-receipt/v2` |
| reference producer/store/output | `aspx-reference/v2`, `aspx-reference-sqlite/v2`, `aspx-reference-output/v2` |
| aggregate | `aspx-acquisition-verdict/v2` |
| live provider | `sharepoint-live-aspx-provider/v2` |
| terminal receipt | `aspx-acquisition-terminal-receipt/v1` |

## Transport and semantic denial

`PnPContextSharePointAspxRestClient.GetPageAsync` delegates response handling to `SharePointRestResponseParser`. It evaluates both transport status and the response body before parsing collection items.

- HTTP `401` and `403` are terminal `Denied`.
- An HTTP-success HTML sign-in shell is terminal `Denied` with `semanticDetectorResult=login-shell`.
- An HTTP-success SharePoint/ODATA access-denied or unauthorized error envelope is terminal `Denied`.
- A non-denial error envelope is `Failed` and is never admitted as a successful item.
- Invalid JSON is `Failed`.

No semantic denial body is returned as an item collection.

## Structured request receipt

Each `AspxPaginationPageReceipt` v2 persists:

- actual method, request endpoint, select and filter;
- nullable actual HTTP status when no response exists;
- semantic detector result;
- response SHA-256;
- UTC receipt time;
- actual attempt count and configured attempt limit;
- request and correlation IDs when the service provides them;
- structured error code;
- pagination ordinal, request/next token hashes, item count and terminal flag.

Authentication headers, cookies, passwords, certificates, private keys and access tokens are never copied to the receipt. Raw continuation tokens remain hashed.

Denied, failed and unknown surfaces retain `expectedCountState=Unknown`, `expectedCount=null`, their terminal outcome, and their denominator row. They are not converted to empty or complete.

## User Information List Forms disposition

The live evidence at SharePoint build `16.0.27709.12000` showed the selected ordinary-list `Forms` endpoint succeeding while `_catalogs/users` (`BaseTemplate=112`) returned HTTP `400` under the same provider class.

`AspxSystemListFormsPolicy` applies the immutable code rule `sharepoint-user-information-list-forms-http-400/v1` only when all of these facts are present:

- actual list template is `112`;
- actual root folder ends in `/_catalogs/users`;
- the actual `Forms` request returns HTTP `400`.

The rule does not declare NotApplicable. It records `SystemOrVirtualOnly`, `Failed`, `expectedCount=Unknown`, `ReferenceUnavailable`, the exact platform binding and evidence refs, and the classification effect `historical-virtual-unknown`. Historical, ghosted, setup and virtual ASPX applicability therefore remains explicit and unresolved.

## Product terminal receipt

`--terminal-receipt` is required for `aspx-acquisition`. The handler generates one atomic product-owned terminal receipt after the command outcome is known.

The receipt records the actual nullable contract field `exitCode`; the product writer always supplies the command's integer return code and never converts an absent external value to zero. It also binds:

- product ref, SDK ref, artifact run ID and snapshot fence;
- completion state and aggregate verdict when available;
- managed assembly/package/informational version;
- executable SHA-256 and length;
- referenced-assembly dependency graph SHA-256 and count;
- `.deps.json` SHA-256 and length when present;
- SHA-256, length, role and output version for physical SQLite, physical JSON, reference SQLite, reference JSON and aggregate JSON.

A zero exit is invalid unless all five official volumes are present exactly once. Fresh readback uses `AspxTerminalRunReceiptValidator`.

## Resume compatibility

Reference v1 ledgers are not silently upgraded or overwritten. A v2 resume validates the stored output, reference producer/store/provider versions and the complete immutable manifest before physical enumeration resumes. Incompatible v1 or drifted provenance produces an actionable rejection that instructs the operator to preserve the prior ledger and start a new v2 run with new output paths.

An existing terminal receipt on resume must bind the same run ID, product ref and snapshot fence. A mismatched or invalid receipt is preserved rather than overwritten.

## CLI output set

The live command now requires distinct paths for:

```text
--physical-database
--physical-output
--reference-database
--reference-output
--aggregate-output
--terminal-receipt
```

New runs refuse any pre-existing output path. Explicit resume remains required to update an existing same-run output set.

## Evidence sources

- Product baseline: `pnp/assessment@3012555317d5a8ee981b9e103206f3f0680333d8`.
- Sealed live run: `62dbcc5b-80fd-4055-b528-745f9451ef2a`.
- Independent verdict: CCD-726.
- Bounded provider diagnosis: CCD-734.
- Reusable KB fact: `dev.titao/main@4c91e3a3e0544d871c7faad9f5d029b7ce55e035`.

These inputs prove the need for fail-closed classification and provenance. They do not prove tenant-wide completeness or authorize a NotApplicable disposition for system-list Forms.
