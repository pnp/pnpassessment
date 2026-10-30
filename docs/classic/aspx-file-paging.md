# Physical ASPX file paging

Physical folder-file discovery uses a maximum requested page size of **1,000 files** and a **16 MiB response-body limit**. These are client resource policies, not claims about a SharePoint service maximum.

## Size backoff

The current cursor is retried at 500 and then 200 rows only when the JSON response exceeds its byte limit, or the endpoint returns more entries than the requested page bound. The smaller size remains in use for that collection. At 200, another size failure stops the collection as incomplete; already acquired file facts are retained.

HTTP 206, malformed JSON, permission failures, transport failures and cancellation do not masquerade as an oversized page. Existing HTTP 429/503 Retry-After handling remains independent and bounded.

## Advancing a page

Files are requested with Name ordering and numeric offsets. When a safe server continuation exists it is followed; otherwise the offset advances by the actual response count. A missing nextLink on a capped response does not establish completion: an explicit empty page is required. Invalid/foreign continuations, repeated identities or the 10,000-request collection budget prevent a successful-completeness claim.

This is an observed traversal, not a transactional snapshot. Concurrent insertion, deletion or renaming can change an offset-ordered collection while it is being scanned. A static-site acceptance run is not proof of snapshot consistency.

## Evidence and overlapping roots

The successful/terminal page sequence forms the validated pagination chain. All completed size-backoff attempts retain their endpoint, status, response digest, request/correlation identifiers and timestamp in the surface evidence, including attempts completed before cancellation. A recovered size failure does not itself become a separate failed inventory authority.

SDK folder reads acquire only folder identity metadata. They do not expand Files or Folders outside the bounded REST path. Complete physical collection results can be reused across overlapping entrances; incomplete, denied, truncated and cancelled results cannot authorize a complete cache entry.

These limits do not change the separation between raw discovery evidence, physical ASPX inventory and existing Classic business-page assessment/export.
