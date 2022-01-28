using CsvHelper;
using CsvHelper.Configuration;
using PnP.Scanning.Core.Storage;
using System.Globalization;

namespace PnP.Scanning.Core.Services
{
    internal sealed class ReportManager
    {
        
        public ReportManager()
        {
        }

        internal async Task ExportReportDataAsync(Guid scanId, string? exportPath = null, string? delimiter = null)
        {

            // Export the data from the SQLite store
            if (string.IsNullOrEmpty(exportPath))
            {
                exportPath = Path.Join(StorageManager.GetScanDataFolder(scanId), "report");
            }

            // Ensure path exists
            Directory.CreateDirectory(exportPath);

            using (var dbContext = StorageManager.GetScanContextForDataExport(scanId))
            {
                var config = new CsvConfiguration(CultureInfo.InvariantCulture)
                {
                    Delimiter = delimiter,
                };

                using (var writer = new StreamWriter(Path.Join(exportPath, "scans.csv")))
                {
                    using (var csv = new CsvWriter(writer, config))
                    {
                        await csv.WriteRecordsAsync(dbContext.Scans.AsAsyncEnumerable());
                    }
                }

                using (var writer = new StreamWriter(Path.Join(exportPath, "properties.csv")))
                {
                    using (var csv = new CsvWriter(writer, config))
                    {
                        await csv.WriteRecordsAsync(dbContext.Properties.AsAsyncEnumerable());
                    }
                }

                using (var writer = new StreamWriter(Path.Join(exportPath, "history.csv")))
                {
                    using (var csv = new CsvWriter(writer, config))
                    {
                        await csv.WriteRecordsAsync(dbContext.History.AsAsyncEnumerable());
                    }
                }

                using (var writer = new StreamWriter(Path.Join(exportPath, "sitecollections.csv")))
                {
                    using (var csv = new CsvWriter(writer, config))
                    {
                        await csv.WriteRecordsAsync(dbContext.SiteCollections.AsAsyncEnumerable());
                    }
                }

                using (var writer = new StreamWriter(Path.Join(exportPath, "webs.csv")))
                {
                    using (var csv = new CsvWriter(writer, config))
                    {
                        await csv.WriteRecordsAsync(dbContext.Webs.AsAsyncEnumerable());
                    }
                }

                using (var writer = new StreamWriter(Path.Join(exportPath, "testdelays.csv")))
                {
                    using (var csv = new CsvWriter(writer, config))
                    {
                        await csv.WriteRecordsAsync(dbContext.TestDelays.AsAsyncEnumerable());
                    }
                }
            }

        }

    }
}
