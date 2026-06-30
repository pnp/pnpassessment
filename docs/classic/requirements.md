# Requirements

This page lists the classic pages assessment specific requirements and options.

## Permission requirements

When using the classic pages module of the Microsoft 365 Assessment tool you do need to use a configured Entra application ([learn more here](../using-the-assessment-tool/setupauth.md)). The classic pages assessment reads page content and web part configuration via CSOM, so it needs read access to the assessed sites.

Authentication | Minimal
---------------| -------
Application | **Graph:** Sites.Read.All <br> **SharePoint:** Sites.FullControl.All
Delegated | **Graph:** Sites.Read.All, User.Read <br> **SharePoint:** AllSites.FullControl

> [!Note]
> `Sites.FullControl.All` is requested because reading a classic web part page's web part inventory via the `LimitedWebPartManager` API requires more than read-only access. If a future release can lower this requirement it will be reflected here.

### Additional permission for audit log usage collection

The classic pages assessment automatically collects audit log data (page view/create/edit counts per classic page) from the **Microsoft Graph audit log API**. This requires one additional **application** permission on the Entra app, which must be added manually in the Entra Portal after the app is created:

Authentication | Additional permission
---------------| -------
Application | **Microsoft Graph:** AuditLogsQuery-SharePoint.Read.All

To add this permission: go to the Entra Portal → **App registrations** → select your app → **API permissions** → **Add a permission** → **Microsoft Graph** → **Application permissions** → search for `AuditLogsQuery` → select `AuditLogsQuery-SharePoint.Read.All` → **Add permissions** → **Grant admin consent**.

> [!Note]
> If this permission is missing, audit log collection is skipped and the `classicpageauditusage.csv` rows will show `QueryStatus=skipped` and `SkipReason=NoPermission`. The rest of the assessment (page discovery, web part mapping, etc.) is not affected.

> [!Note]
> Audit logging must be enabled for the tenant before running the assessment. Go to [Microsoft Purview compliance portal](https://compliance.microsoft.com) → **Audit** → click **Start recording user and admin activity** if not already enabled. Audit events are available in the log approximately 60–90 minutes after they occur.

## Command line arguments for starting an assessment

The classic pages assessment is selected with `--mode Classic` and the `--classicinclude Pages` component. The following page-scan specific arguments are available (they are only valid together with `--mode Classic`):

Argument | Description
---------|------------
`--classicinclude Pages` | Include the classic page scan in a classic assessment. When `--classicinclude` is omitted all classic components are included.
`--exportwebpartproperties` | Also export each web part's properties (as JSON) into the per-web-part inventory. Off by default to keep the export compact.
`--skipusageinformation` | Skip collecting audit log usage statistics (ClassicPageViewed / ClassicPageCreated / ClassicPageEdited). When set, `classicpageauditusage.csv` is not generated. Use this to speed up the assessment when audit log data is not needed.
`--auditlogwindowdays` | Number of days back to query the audit log (1–180, default 14). Requires the `AuditLogsQuery-SharePoint.Read.All` permission. Audit Standard retention is 180 days; Audit Premium (E5) retains up to 1 year.
`--skipuserinformation` | Skip collecting page user information (e.g. the page's *Modified By*).
`--homepageonly` | Only assess the home page of each web, rather than every classic page. Useful for a fast home-page modernization-readiness assessment.

> [!Note]
> To learn more about starting an assessment checkout the Microsoft 365 Assessment tool [Start documentation](../using-the-assessment-tool/assess-start.md).
