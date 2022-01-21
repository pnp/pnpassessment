using Serilog;

namespace PnP.Scanning.Core.Services
{
    internal sealed class SiteEnumerationManager
    {

#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
        internal async Task<List<string>> EnumerateSiteCollectionsToScanAsync(StartRequest start)
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
        {
            List<string> list = new();

            Log.Information("Building list of site collections to scan");

            if (!string.IsNullOrEmpty(start.SitesList))
            {
                Log.Information("Building list of site collections: using sites list");
                foreach (var site in LoadSitesFromList(start.SitesList, new char[] { ',' }))
                {
                    list.Add(site.TrimEnd('/'));
                }
            }
            else if (!string.IsNullOrEmpty(start.SitesFile))
            {
                Log.Information("Building list of site collections: using sites file");
                foreach (var row in LoadSitesFromCsv(start.SitesFile, new char[] { ',' }))
                {
                    if (!string.IsNullOrEmpty(row[0]))
                    {
                        list.Add(row[0].ToString().TrimEnd('/'));
                    }
                }
            }
            else if (!string.IsNullOrEmpty(start.Tenant))
            {
                Log.Information("Building list of site collections: using tenant scope");

            }

#if DEBUG
            if (!string.IsNullOrEmpty(start.Mode) && 
                start.Mode.Equals("test", StringComparison.OrdinalIgnoreCase) &&
                list.Count == 0)
            {
                int sitesToScan = 10;
                var numberOfSitesProperty = start.Properties.FirstOrDefault(p => p.Property == Constants.StartTestNumberOfSites);

                if (numberOfSitesProperty != null)
                {
                    sitesToScan = int.Parse(numberOfSitesProperty.Value);
                }

                for (int i = 0; i < sitesToScan; i++)
                {
                    list.Add($"https://bertonline.sharepoint.com/sites/prov-{i}");
                }
            }
#endif
            Log.Information("Scan scope defined: {SitesToScan} site collections will be scanned", list.Count);

            return list;
        }

        /// <summary>
        /// Load csv file and return data
        /// </summary>
        /// <param name="path">Path to CSV file</param>
        /// <param name="separator">Separator used in the CSV file</param>
        /// <returns>List of site collections</returns>
        private static IEnumerable<string[]> LoadSitesFromCsv(string path, params char[] separator)
        {
            return from line in File.ReadLines(path)
                   let parts = from p in line.Split(separator, StringSplitOptions.RemoveEmptyEntries) select p
                   select parts.ToArray();
        }

        private static string[] LoadSitesFromList(string list, params char[] separator)
        {
            return list.Split(separator, StringSplitOptions.RemoveEmptyEntries);                   
        }
    }
}
