﻿using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Storage;
using Serilog;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace PnP.Scanning.Core.Services
{
    internal sealed class ReportManager
    {
        private const string ReportFolder = "report";
        private const string ScansCsv = "scans.csv";
        private const string PropertiesCsv = "properties.csv";
        private const string HistoryCsv = "history.csv";
        private const string SitecollectionsCsv = "sitecollections.csv";
        private const string WebsCsv = "webs.csv";

        //PER SCAN COMPONENT: define tables to export to csv
        private const string SyntexListsCsv = "syntexlists.csv";
        private const string SyntexContentTypesCsv = "syntexcontenttypes.csv";
        private const string SyntexContentTypeFieldsCsv = "syntexcontentfields.csv";
        private const string SyntexContentTypeOverviewCsv = "syntexcontenttypeoverview.csv";
        private const string SyntexFieldsCsv = "syntexfields.csv";
        private const string SyntexModelUsageCsv = "syntexmodelusage.csv";
        private const string SyntexFileTypesCsv = "syntexfiletypes.csv";
        private const string SyntexTermSetsCsv = "syntextermsets.csv";

        private const string WorkflowsCsv = "workflows.csv";

        private const string ClassicInfoPathCsv = "classicinfopath.csv";
        private const string ClassicPagesCsv = "classicpages.csv";
        private const string ClassicListsCsv = "classiclists.csv";
        private const string ClassicUserCustomActionsCsv = "classicusercustomactions.csv";
        private const string ClassicExtensibilitiesCsv = "classicextensibilities.csv";
        private const string ClassicWebSummariesCsv = "classicwebsummaries.csv";
        private const string ClassicSiteSummariesCsv = "classicsitesummaries.csv";
        private const string ClassicWebPartsCsv = "classicwebparts.csv";

        private const string ClassicAddInsCsv = "classicaddins.csv";
        private const string ClassicACSPrincipalsCsv = "classicacsprincipals.csv";
        private const string ClassicACSPrincipalSitesCsv = "classicacsprincipalsites.csv";
        private const string ClassicACSPrincipalSiteScopedPermissionsCsv = "classicacsprincipalsitescopedpermissions.csv";
        private const string ClassicACSPrincipalTenantScopedPermissionsCsv = "classicacsprincipaltenantcopedpermissions.csv";

        private const string AlertsCsv = "alerts.csv";

#if DEBUG
        private const string TestDelaysCsv = "testdelays.csv";
#endif

        public ReportManager()
        {
        }

        internal async Task<string> CreatePowerBiReportAsync(Guid scanId, string exportPath = null, string delimiter = null)
        {
            string reportFile = "";
            exportPath = EnsureReportPath(scanId, exportPath);

            using (var dbContext = await StorageManager.GetScanContextForDataExportAsync(scanId))
            {
                var scan = await dbContext.Scans.Where(p => p.ScanId == scanId).FirstOrDefaultAsync();

                // PER SCAN COMPONENT: Update report data per scan component
                if (scan.CLIMode == Mode.Syntex.ToString())
                {
                    reportFile = "SyntexAssessmentReport.pbit";
                    // Put the report file in the report folder
                    string pbitFile = Path.Combine(exportPath, reportFile);
                    PersistPBitFromResource("PnP.Scanning.Core.Scanners.Syntex.SyntexAssessmentReport.pbit", pbitFile);

                    // Update the report file to pick up the exported CSV files in the report folder
                    // Below are the hardcoded values used for path and delimiter when the template PowerBi was created
                    RewriteDataLocationsInPbit(pbitFile, delimiter, "q:\\\\github\\\\pnpassessment\\\\src\\\\PnP.Scanning\\\\Reports\\\\Syntex\\\\", ",");
                }
                else if (scan.CLIMode == Mode.Workflow.ToString())
                {
                    reportFile = "WorkflowReport.pbit";
                    // Put the report file in the report folder
                    string pbitFile = Path.Combine(exportPath, reportFile);
                    PersistPBitFromResource("PnP.Scanning.Core.Scanners.Workflow.WorkflowReport.pbit", pbitFile);

                    // Update the report file to pick up the exported CSV files in the report folder
                    // Below are the hardcoded values used for path and delimiter when the template PowerBi was created
                    RewriteDataLocationsInPbit(pbitFile, delimiter, "q:\\\\github\\\\pnpassessment\\\\src\\\\PnP.Scanning\\\\Reports\\\\Workflow\\\\", ",");
                }
                else if (scan.CLIMode == Mode.Classic.ToString())
                {
                    reportFile = "ClassicAssessmentReport.pbit";
                    // Put the report file in the report folder
                    string pbitFile = Path.Combine(exportPath, reportFile);
                    PersistPBitFromResource("PnP.Scanning.Core.Scanners.Classic.ClassicAssessmentReport.pbit", pbitFile);

                    // Update the report file to pick up the exported CSV files in the report folder
                    // Below are the hardcoded values used for path and delimiter when the template PowerBi was created
                    RewriteDataLocationsInPbit(pbitFile, delimiter, "q:\\\\github\\\\pnpassessment\\\\src\\\\PnP.Scanning\\\\Reports\\\\Classic\\\\", ",");
                }
                else if (scan.CLIMode == Mode.InfoPath.ToString())
                {
                    reportFile = "InfoPathAssessmentReport.pbit";
                    // Put the report file in the report folder
                    string pbitFile = Path.Combine(exportPath, reportFile);
                    PersistPBitFromResource("PnP.Scanning.Core.Scanners.InfoPath.InfoPathAssessmentReport.pbit", pbitFile);

                    // Update the report file to pick up the exported CSV files in the report folder
                    // Below are the hardcoded values used for path and delimiter when the template PowerBi was created
                    RewriteDataLocationsInPbit(pbitFile, delimiter, "q:\\\\github\\\\pnpassessment\\\\src\\\\PnP.Scanning\\\\Reports\\\\InfoPath\\\\", ",");
                }
                else if (scan.CLIMode == Mode.AddInsACS.ToString())
                {
                    reportFile = "AddInsACSAssessmentReport.pbit";
                    // Put the report file in the report folder
                    string pbitFile = Path.Combine(exportPath, reportFile);
                    PersistPBitFromResource("PnP.Scanning.Core.Scanners.AddInsACS.AddInsACSAssessmentReport.pbit", pbitFile);

                    // Update the report file to pick up the exported CSV files in the report folder
                    // Below are the hardcoded values used for path and delimiter when the template PowerBi was created
                    RewriteDataLocationsInPbit(pbitFile, delimiter, "q:\\\\github\\\\pnpassessment\\\\src\\\\PnP.Scanning\\\\Reports\\\\AddInsACS\\\\", ",");
                }
                else if (scan.CLIMode == Mode.Alerts.ToString())
                {
                    reportFile = "AlertsAssessmentReport.pbit";
                    // Put the report file in the report folder
                    string pbitFile = Path.Combine(exportPath, reportFile);
                    PersistPBitFromResource("PnP.Scanning.Core.Scanners.Alerts.AlertsAssessmentReport.pbit", pbitFile);

                    // Update the report file to pick up the exported CSV files in the report folder
                    // Below are the hardcoded values used for path and delimiter when the template PowerBi was created
                    RewriteDataLocationsInPbit(pbitFile, delimiter, "q:\\\\github\\\\pnpassessment\\\\src\\\\PnP.Scanning\\\\Reports\\\\Alerts\\\\", ",");
                }
#if DEBUG
                else if (scan.CLIMode == Mode.Test.ToString())
                {
                    reportFile = "TestReport.pbit";
                    // Put the report file in the report folder
                    string pbitFile = Path.Combine(exportPath, reportFile);
                    PersistPBitFromResource("PnP.Scanning.Core.Scanners.Test.TestReport.pbit", pbitFile);

                    // Update the report file to pick up the exported CSV files in the report folder
                    // Below are the hardcoded values used for path and delimiter when the template PowerBi was created
                    RewriteDataLocationsInPbit(pbitFile, delimiter, "q:\\\\github\\\\pnpassessment\\\\src\\\\PnP.Scanning\\\\Reports\\\\Test\\\\", ";");
                }
#endif               

                return Path.Combine(exportPath, reportFile);
            }
        }

        internal async Task<string> ExportReportDataAsync(Guid scanId, string exportPath = null, string delimiter = null)
        {
            exportPath = EnsureReportPath(scanId, exportPath);

            using (var dbContext = await StorageManager.GetScanContextForDataExportAsync(scanId))
            {
                var config = new CsvConfiguration(CultureInfo.InvariantCulture)
                {
                    Delimiter = delimiter,
                };

                var scan = await dbContext.Scans.Where(p => p.ScanId == scanId).FirstOrDefaultAsync();
                if (scan == null)
                {
                    Log.Error("There was no assessment result found for assessment {ScanId}", scanId);
                    throw new Exception($"There was no assessment result found for assessment {scanId}");
                }

                if (scan.Status != ScanStatus.Finished && scan.Status != ScanStatus.Paused)
                {
                    Log.Error("Assessment {ScanId} was not Finished or Paused, can't export the data", scanId);
                    throw new Exception($"Assessment {scanId} was not Finished or Paused, can't export the data");
                }

                using (var writer = new StreamWriter(Path.Join(exportPath, ScansCsv)))
                {
                    using (var csv = new CsvWriter(writer, config))
                    {
                        await csv.WriteRecordsAsync(dbContext.Scans.Where(p => p.ScanId == scanId).AsAsyncEnumerable());
                    }
                }

                using (var writer = new StreamWriter(Path.Join(exportPath, PropertiesCsv)))
                {
                    using (var csv = new CsvWriter(writer, config))
                    {
                        await csv.WriteRecordsAsync(dbContext.Properties.Where(p => p.ScanId == scanId).AsAsyncEnumerable());
                    }
                }

                using (var writer = new StreamWriter(Path.Join(exportPath, HistoryCsv)))
                {
                    using (var csv = new CsvWriter(writer, config))
                    {
                        await csv.WriteRecordsAsync(dbContext.History.Where(p => p.ScanId == scanId).AsAsyncEnumerable());
                    }
                }

                using (var writer = new StreamWriter(Path.Join(exportPath, SitecollectionsCsv)))
                {
                    using (var csv = new CsvWriter(writer, config))
                    {
                        await csv.WriteRecordsAsync(dbContext.SiteCollections.Where(p => p.ScanId == scanId).AsAsyncEnumerable());
                    }
                }

                using (var writer = new StreamWriter(Path.Join(exportPath, WebsCsv)))
                {
                    using (var csv = new CsvWriter(writer, config))
                    {
                        await csv.WriteRecordsAsync(dbContext.Webs.Where(p => p.ScanId == scanId).AsAsyncEnumerable());
                    }
                }

                #region Scanner specific export
                // PER SCAN COMPONENT: define export for the scan specific tables

                if (scan.CLIMode == Mode.Syntex.ToString())
                {
                    using (var writer = new StreamWriter(Path.Join(exportPath, SyntexListsCsv)))
                    {
                        using (var csv = new CsvWriter(writer, config))
                        {
                            await csv.WriteRecordsAsync(dbContext.SyntexLists.Where(p => p.ScanId == scanId).AsAsyncEnumerable());
                        }
                    }

                    using (var writer = new StreamWriter(Path.Join(exportPath, SyntexContentTypesCsv)))
                    {
                        using (var csv = new CsvWriter(writer, config))
                        {
                            await csv.WriteRecordsAsync(dbContext.SyntexContentTypes.Where(p => p.ScanId == scanId).AsAsyncEnumerable());
                        }
                    }

                    using (var writer = new StreamWriter(Path.Join(exportPath, SyntexContentTypeFieldsCsv)))
                    {
                        using (var csv = new CsvWriter(writer, config))
                        {
                            await csv.WriteRecordsAsync(dbContext.SyntexContentTypeFields.Where(p => p.ScanId == scanId).AsAsyncEnumerable());
                        }
                    }

                    using (var writer = new StreamWriter(Path.Join(exportPath, SyntexContentTypeOverviewCsv)))
                    {
                        using (var csv = new CsvWriter(writer, config))
                        {
                            await csv.WriteRecordsAsync(dbContext.SyntexContentTypeOverview.Where(p => p.ScanId == scanId).AsAsyncEnumerable());
                        }
                    }

                    using (var writer = new StreamWriter(Path.Join(exportPath, SyntexFieldsCsv)))
                    {
                        using (var csv = new CsvWriter(writer, config))
                        {
                            await csv.WriteRecordsAsync(dbContext.SyntexFields.Where(p => p.ScanId == scanId).AsAsyncEnumerable());
                        }
                    }

                    using (var writer = new StreamWriter(Path.Join(exportPath, SyntexModelUsageCsv)))
                    {
                        using (var csv = new CsvWriter(writer, config))
                        {
                            await csv.WriteRecordsAsync(dbContext.SyntexModelUsage.Where(p => p.ScanId == scanId).AsAsyncEnumerable());
                        }
                    }

                    using (var writer = new StreamWriter(Path.Join(exportPath, SyntexFileTypesCsv)))
                    {
                        using (var csv = new CsvWriter(writer, config))
                        {
                            await csv.WriteRecordsAsync(dbContext.SyntexFileTypes.Where(p => p.ScanId == scanId).AsAsyncEnumerable());
                        }
                    }

                    using (var writer = new StreamWriter(Path.Join(exportPath, SyntexTermSetsCsv)))
                    {
                        using (var csv = new CsvWriter(writer, config))
                        {
                            await csv.WriteRecordsAsync(dbContext.SyntexTermSets.Where(p => p.ScanId == scanId).AsAsyncEnumerable());
                        }
                    }
                }

                if (scan.CLIMode == Mode.Workflow.ToString())
                {
                    using (var writer = new StreamWriter(Path.Join(exportPath, WorkflowsCsv)))
                    {
                        using (var csv = new CsvWriter(writer, config))
                        {
                            await csv.WriteRecordsAsync(dbContext.Workflows.Where(p => p.ScanId == scanId).AsAsyncEnumerable());
                        }
                    }
                }

                if (scan.CLIMode == Mode.Classic.ToString())
                {
                    using (var writer = new StreamWriter(Path.Join(exportPath, WorkflowsCsv)))
                    {
                        using (var csv = new CsvWriter(writer, config))
                        {
                            await csv.WriteRecordsAsync(dbContext.Workflows.Where(p => p.ScanId == scanId).AsAsyncEnumerable());
                        }
                    }

                    using (var writer = new StreamWriter(Path.Join(exportPath, ClassicExtensibilitiesCsv)))
                    {
                        using (var csv = new CsvWriter(writer, config))
                        {
                            await csv.WriteRecordsAsync(dbContext.ClassicExtensibilities.Where(p => p.ScanId == scanId).AsAsyncEnumerable());
                        }
                    }

                    using (var writer = new StreamWriter(Path.Join(exportPath, ClassicInfoPathCsv)))
                    {
                        using (var csv = new CsvWriter(writer, config))
                        {
                            await csv.WriteRecordsAsync(dbContext.ClassicInfoPath.Where(p => p.ScanId == scanId).AsAsyncEnumerable());
                        }
                    }

                    using (var writer = new StreamWriter(Path.Join(exportPath, ClassicListsCsv)))
                    {
                        using (var csv = new CsvWriter(writer, config))
                        {
                            await csv.WriteRecordsAsync(dbContext.ClassicLists.Where(p => p.ScanId == scanId).AsAsyncEnumerable());
                        }
                    }

                    using (var writer = new StreamWriter(Path.Join(exportPath, ClassicPagesCsv)))
                    {
                        using (var csv = new CsvWriter(writer, config))
                        {
                            await csv.WriteRecordsAsync(dbContext.ClassicPages.Where(p => p.ScanId == scanId).AsAsyncEnumerable());
                        }
                    }

                    using (var writer = new StreamWriter(Path.Join(exportPath, ClassicUserCustomActionsCsv)))
                    {
                        using (var csv = new CsvWriter(writer, config))
                        {
                            await csv.WriteRecordsAsync(dbContext.ClassicUserCustomActions.Where(p => p.ScanId == scanId).AsAsyncEnumerable());
                        }
                    }

                    using (var writer = new StreamWriter(Path.Join(exportPath, ClassicSiteSummariesCsv)))
                    {
                        using (var csv = new CsvWriter(writer, config))
                        {
                            await csv.WriteRecordsAsync(dbContext.ClassicSiteSummaries.Where(p => p.ScanId == scanId).AsAsyncEnumerable());
                        }
                    }

                    using (var writer = new StreamWriter(Path.Join(exportPath, ClassicWebPartsCsv)))
                    {
                        using (var csv = new CsvWriter(writer, config))
                        {
                            await csv.WriteRecordsAsync(dbContext.ClassicWebParts.Where(p => p.ScanId == scanId).AsAsyncEnumerable());
                        }
                    }

                    using (var writer = new StreamWriter(Path.Join(exportPath, ClassicWebSummariesCsv)))
                    {
                        using (var csv = new CsvWriter(writer, config))
                        {
                            await csv.WriteRecordsAsync(dbContext.ClassicWebSummaries.Where(p => p.ScanId == scanId).AsAsyncEnumerable());
                        }
                    }
                }

                if (scan.CLIMode == Mode.InfoPath.ToString())
                {
                    using (var writer = new StreamWriter(Path.Join(exportPath, ClassicInfoPathCsv)))
                    {
                        using (var csv = new CsvWriter(writer, config))
                        {
                            await csv.WriteRecordsAsync(dbContext.ClassicInfoPath.Where(p => p.ScanId == scanId).AsAsyncEnumerable());
                        }
                    }
                }

                if (scan.CLIMode == Mode.AddInsACS.ToString())
                {
                    using (var writer = new StreamWriter(Path.Join(exportPath, ClassicAddInsCsv)))
                    {
                        using (var csv = new CsvWriter(writer, config))
                        {
                            await csv.WriteRecordsAsync(dbContext.ClassicAddIns.Where(p => p.ScanId == scanId).AsAsyncEnumerable());
                        }
                    }

                    using (var writer = new StreamWriter(Path.Join(exportPath, ClassicACSPrincipalsCsv)))
                    {
                        using (var csv = new CsvWriter(writer, config))
                        {
                            await csv.WriteRecordsAsync(dbContext.ClassicACSPrincipals.Where(p => p.ScanId == scanId).AsAsyncEnumerable());
                        }
                    }

                    using (var writer = new StreamWriter(Path.Join(exportPath, ClassicACSPrincipalSitesCsv)))
                    {
                        using (var csv = new CsvWriter(writer, config))
                        {
                            await csv.WriteRecordsAsync(dbContext.ClassicACSPrincipalSites.Where(p => p.ScanId == scanId).AsAsyncEnumerable());
                        }
                    }

                    using (var writer = new StreamWriter(Path.Join(exportPath, ClassicACSPrincipalSiteScopedPermissionsCsv)))
                    {
                        using (var csv = new CsvWriter(writer, config))
                        {
                            await csv.WriteRecordsAsync(dbContext.ClassicACSPrincipalSiteScopedPermissions.Where(p => p.ScanId == scanId).AsAsyncEnumerable());
                        }
                    }

                    using (var writer = new StreamWriter(Path.Join(exportPath, ClassicACSPrincipalTenantScopedPermissionsCsv)))
                    {
                        using (var csv = new CsvWriter(writer, config))
                        {
                            await csv.WriteRecordsAsync(dbContext.ClassicACSPrincipalTenantScopedPermissions.Where(p => p.ScanId == scanId).AsAsyncEnumerable());
                        }
                    }
                }

                if (scan.CLIMode == Mode.Alerts.ToString())
                {
                    using (var writer = new StreamWriter(Path.Join(exportPath, AlertsCsv)))
                    {
                        using (var csv = new CsvWriter(writer, config))
                        {
                            await csv.WriteRecordsAsync(dbContext.Alerts.Where(p => p.ScanId == scanId).AsAsyncEnumerable());
                        }
                    }
                }

#if DEBUG
                if (scan.CLIMode == Mode.Test.ToString())
                {
                    using (var writer = new StreamWriter(Path.Join(exportPath, TestDelaysCsv)))
                    {
                        using (var csv = new CsvWriter(writer, config))
                        {
                            await csv.WriteRecordsAsync(dbContext.TestDelays.Where(p => p.ScanId == scanId).AsAsyncEnumerable());
                        }
                    }
                }
#endif

#endregion
            }

            return exportPath;
        }

        private static string EnsureReportPath(Guid scanId, string exportPath)
        {
            // Export the data from the SQLite store
            if (string.IsNullOrEmpty(exportPath))
            {
                exportPath = Path.Join(StorageManager.GetScanDataFolder(scanId), ReportFolder);
            }

            // Ensure path exists
            Directory.CreateDirectory(exportPath);
            return exportPath;
        }

        private static void PersistPBitFromResource(string pbitIdentifier, string pbitFile)
        {
            File.WriteAllBytes(pbitFile, LoadResourceBytes(pbitIdentifier));
        }

        private static byte[] LoadResourceBytes(string resource)
        {
            using (Stream stream = typeof(ReportManager).Assembly.GetManifestResourceStream(resource))
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    stream.CopyTo(ms);
                    return ms.ToArray();
                }
            }
        }

        private static void RewriteDataLocationsInPbit(string pbitFile, string newDelimiter, string oldLocation, string oldDelimiter)
        {
            string destinationPath = "";
            string copiedFile = "";

            if (string.IsNullOrEmpty(pbitFile))
            {
                throw new ArgumentNullException(nameof(pbitFile));
            }

            if (!File.Exists(pbitFile))
            {
                throw new Exception($"Pbit file {pbitFile} does not exist");
            }

            string newLocation = Path.GetDirectoryName(pbitFile);
            string extractPath = newLocation;

            if (!newLocation.EndsWith(@"\"))
            {
                newLocation = newLocation + @"\";
            }

            newLocation = newLocation.Replace(@"\", @"\\");

            using (ZipArchive archive = ZipFile.Open(pbitFile, ZipArchiveMode.Update))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    // Extract the current DataModelSchema file
                    if (entry.FullName.Equals("DataModelSchema", StringComparison.OrdinalIgnoreCase))
                    {
                        // Gets the full path to ensure that relative segments are removed.
                        destinationPath = Path.GetFullPath(Path.Combine(extractPath, entry.FullName));

                        // Ordinal match is safest, case-sensitive volumes can be mounted within volumes that
                        // are case-insensitive.
                        if (destinationPath.StartsWith(extractPath, StringComparison.Ordinal))
                        {
                            entry.ExtractToFile(destinationPath);

                            // Delete file from archive
                            entry.Delete();

                            break;
                        }
                    }
                }

                copiedFile = destinationPath + ".bak";
                File.Copy(destinationPath, copiedFile, true);
                File.Delete(destinationPath);

                using (var sw = new StreamWriter(destinationPath, false, Encoding.Unicode))
                using (var fs = File.OpenRead(copiedFile))
                using (var sr = new StreamReader(fs, Encoding.Unicode))
                {
                    string line, newLine;

                    while ((line = sr.ReadLine()) != null)
                    {
                        newLine = line.Replace(oldLocation, newLocation, StringComparison.InvariantCultureIgnoreCase);

                        if (newDelimiter != null && newDelimiter != oldDelimiter)
                        {
                            newLine = newLine.Replace($"Delimiter=\\\"{oldDelimiter}\\\"", $"Delimiter=\\\"{newDelimiter}\\\"");
                        }

                        sw.WriteLine(newLine);
                    }
                }

                // Drop first 2 bytes to get rid of the BOM (\uFEFF)
                var bytes = File.ReadAllBytes(destinationPath);
                File.WriteAllBytes(destinationPath, bytes.Skip(2).ToArray());

                // Add the modified DataModelSchema file back to the archive
                archive.CreateEntryFromFile(destinationPath, "DataModelSchema");
            }

            // Delete the exported DataModelSchema file
            File.Delete(destinationPath);
            File.Delete(copiedFile);
        }

    }
}
