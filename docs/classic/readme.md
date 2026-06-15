# Classic SharePoint Pages Assessment

Modern SharePoint pages are the recommended, supported way to build pages in SharePoint Online. Many tenants still contain **classic** pages — wiki pages, web part pages and publishing pages — that predate the modern page experience. The classic pages assessment helps you understand where classic pages live in your tenant and how ready those pages are to be modernized: for each classic page it inventories the web parts on the page and calculates a **mapping percentage** that indicates how many of those web parts have a modern equivalent.

This module carries the classic page scanning capability of the older [SharePoint Modernization Scanner](https://aka.ms/sharepoint/modernization/scanner) forward into the Microsoft 365 Assessment tool, so you can use a single, supported tool for this assessment.

The assessment provides you with:

- A per-page inventory of the classic pages found (page type, layout, home-page flags, last modified, usage) — see [classicpages.csv](csv-classicpages.md).
- A per-web-part inventory of every web part found on those pages — see [classicpagewebparts.csv](csv-classicpagewebparts.md).
- A tenant-wide inventory of the unique web part types encountered and whether each is known to the modern mapping model — see [classicwebpartunique.csv](csv-classicwebpartunique.md).
- Web- and site-level [readiness roll-ups](csv-classicwebsummaries.md) and a [Power BI report](report-intro.md) to visualize the results.

> [!IMPORTANT]
> The classic pages assessment is **pre-release**: it is implemented, tested and runnable from a development build via `start --mode Classic --classicinclude Pages`, but is not yet part of a shipped release. Within a Classic assessment only the implemented components run (Pages, Lists, Workflow, InfoPath, Extensibility) — the Azure ACS and SharePoint Add-Ins assessments are provided by the dedicated [`--mode AddInsACS`](../addinsacs/readme.md) module. Track availability via the [releases](https://github.com/pnp/pnpassessment/releases) page.

Use the left navigation to learn how to [run the assessment](assess.md), the [requirements](requirements.md), and the details of the generated report and CSV files.
