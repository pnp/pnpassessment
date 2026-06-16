# The Power BI report

The generated report is a Power BI report and can be opened in Power BI Desktop (see https://aka.ms/pbidesktopstore to install Power BI Desktop).

When using the report from Power BI Desktop there are some things to understand:

- The report is built using multiple pages, use the bottom tabs to switch to different report pages. The **Classic Page Readiness** page summarizes how ready your classic pages are to be modernized (distribution of pages by mapping percentage, the most common unmapped web parts, and the overall web part inventory).
- When the report is opened from the generated Power BI pbit file it's not saved, meaning if you close Power BI Desktop it will ask you to save the file. You can also save the opened report yourselves using the **save** icon top left; when saving as a Power BI pbix file the data and report are combined into a single file, making it easier for you to move around and share the report.
- If you want to update the visualizations used you can use the toolbar and visualizations and fields sections at the right.

The report is driven by the exported CSV files. Even if you do not use Power BI, the CSV files contain the full assessment data — use the left navigation to learn more about each one:

- [classicpages.csv](csv-classicpages.md) — one row per classic page, including its modernization-readiness columns.
- [classicpagewebparts.csv](csv-classicpagewebparts.md) — one row per web part found on a classic page.
- [classicwebpartunique.csv](csv-classicwebpartunique.md) — one row per unique web part type encountered across the assessment.
- [classicwebsummaries.csv](csv-classicwebsummaries.md) — per-web readiness roll-up.
- [classicsitesummaries.csv](csv-classicsitesummaries.md) — per-site-collection readiness roll-up.
- [classicpublishingsitesummaries.csv](csv-classicpublishingsitesummaries.md) — per-publishing-portal roll-up of master pages and page layouts.
