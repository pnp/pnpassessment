# classicpageauditusage.csv file details

## Summary

For native pipeline reports, this file has one row per **selected** [classicpages.csv](csv-classicpages.md) object when usage was requested. It retains the full Posts locator including `?ID=...`, never the shared renderer's usage. Failed, unsupported and uncollected queries have **empty counts**, not successful zeroes; partial counts remain labelled `partial`. Intentionally specifying `--skipusageinformation` keeps the existing no-usage-file behavior; `classicpages.csv` still carries `UsageQueryStatus=skipped` and nullable counts. Legacy assessments retain the historical behavior described below.

This csv file contains one row per classic page for which audit log activity was collected, plus (when a
site's audit data could not be fully collected) a site-level summary row that surfaces the coverage gap.
It reports how often each classic page was **viewed, created and edited** during the queried audit window,
based on data from the **Microsoft Graph audit log** (`security/auditLog/queries`).

The file is only generated when audit log usage collection runs — that is, when `--skipusageinformation`
is **not** specified and the Entra app has the `AuditLogsQuery-SharePoint.Read.All` Microsoft Graph
application permission (see [requirements](requirements.md)).

> [!NOTE]
> Audit events are typically available in the log 60–90 minutes after they occur, so very recent activity
> may not yet be reflected. The counts are a de-duplicated activity signal (for example a page view is not
> re-logged for the same user and page within a short window), not a raw hit counter.

## Columns

Column|Description
------|-----------
PageUrl | Server-relative URL of the classic page (e.g. `/sites/team/SitePages/Home.aspx`). This matches the `Url` column in [classicpages.csv](csv-classicpages.md), so the two files can be joined on this value. On a site-level summary row (see `QueryStatus`), this is the server-relative site URL.
AuditViewsCount | Number of `ClassicPageViewed` events for this page within the audit window.
AuditCreatesCount | Number of `ClassicPageCreated` events for this page within the audit window.
AuditEditsCount | Number of `ClassicPageEdited` events for this page within the audit window.
AuditUniqueUsers | Approximate number of distinct users across all of the above operations for this page. Capped for very heavily-used pages (the exact count saturates once a page's tracked-user set reaches the internal cap).
AuditWindowStart | Start of the queried audit window (UTC).
AuditWindowEnd | End of the queried audit window (UTC).
QueryStatus | Coverage status for this row. `succeeded` = the audit window was queried in full. `partial` = one or more sub-windows failed, so this page's counts are a floor (some days may be missing). `failed` = audit collection failed for this site (for example a missing permission or a query error); counts are 0. `skipped` = audit collection was intentionally not run for this site (for example an unsupported/sovereign cloud).
SkipReason | Populated when `QueryStatus` is not `succeeded`. A human-readable reason, e.g. `NoPermission: The Entra app is missing the 'AuditLogsQuery-SharePoint.Read.All' application permission...`, `PartialData: 2/7 chunk(s) failed — ...`, or a query/timeout error. Empty on successful rows.
ScanId | Id of the assessment.
SiteUrl | Fully qualified site collection URL that owns this page.

> [!NOTE]
> If the audit query for a site fails or returns partially, that site's affected rows carry
> `QueryStatus=failed`/`partial` with the reason in `SkipReason`. In particular, a missing
> `AuditLogsQuery-SharePoint.Read.All` permission produces `QueryStatus=failed` with
> `SkipReason=NoPermission: ...` — check this column if audit counts are unexpectedly 0.
