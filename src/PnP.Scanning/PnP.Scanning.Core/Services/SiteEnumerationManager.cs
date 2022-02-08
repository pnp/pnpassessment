using PnP.Core.Admin.Model.SharePoint;
using PnP.Core.Auth;
using PnP.Core.Model.SharePoint;
using PnP.Core.Services;
using PnP.Scanning.Core.Authentication;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Storage;
using Serilog;
using System.Linq.Expressions;

namespace PnP.Scanning.Core.Services
{
    internal sealed class SiteEnumerationManager
    {
        private readonly IPnPContextFactory contextFactory;

        public SiteEnumerationManager(StorageManager storageManager, IPnPContextFactory pnpContextFactory)
        {
            StorageManager = storageManager;
            contextFactory = pnpContextFactory;
        }

        internal StorageManager StorageManager { get; private set; }

        internal async Task<List<string>> EnumerateSiteCollectionsToScanAsync(StartRequest start, AuthenticationManager authenticationManager, Action<string> feedback)
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

                feedback.Invoke($"Loaded {list.Count} site collections from the passed siteslist parameter");
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
                feedback.Invoke($"Loaded {list.Count} site collections from the passed file {start.SitesFile}");
            }
            else if (!string.IsNullOrEmpty(start.Tenant))
            {
                Log.Information("Building list of site collections: using tenant scope");

                using (var context = await contextFactory.CreateAsync(new Uri(AuthenticationManager.GetSiteFromTenant(start.Tenant)),
                                                                        new ExternalAuthenticationProvider((resourceUri, scopes) =>
                                                                        {
                                                                            return authenticationManager.GetAccessTokenAsync(scopes).GetAwaiter().GetResult();
                                                                        }
                    )))
                {
                    // Enumerate all site collections
                    var siteCollections = await context.GetSiteCollectionManager().GetSiteCollectionsAsync(filter: SiteCollectionFilter.ExcludePersonalSites);
                    foreach(var siteCollection in siteCollections)
                    {
                        list.Add(siteCollection.Url.ToString());
                    }
                }

                feedback.Invoke($"Enumerated {list.Count} site collections for tenant {start.Tenant}");
            }

#if DEBUG
            // Insert a set of dummy site collections for testing purposes
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

        internal async Task<List<EnumeratedWeb>> EnumerateWebsToScanAsync(Guid scanId, string siteCollectionUrl, OptionsBase options, AuthenticationManager authenticationManager, bool isRestart)
        {
            List<EnumeratedWeb> webUrlsToScan = new();
            
            if (isRestart)
            {
                // When we're enumerating webs for a scan restart we might already have done this 
                // previously and so only the webs not processed should be handled again
                var websToRestart = await StorageManager.WebsToRestartScanningAsync(scanId, siteCollectionUrl);
                if (websToRestart != null && websToRestart.Count > 0)
                {
                    Log.Information("Loaded {Count} webs for restarting scan {ScanId} with site collection {SiteCollectionUrl}", websToRestart.Count, scanId, siteCollectionUrl);
                    return websToRestart;
                }
            }

            // Ensure these props are already loaded when the site is loaded into the PnPContext
            var contextOptions = new PnPContextOptions()
            {
                AdditionalWebPropertiesOnCreate = new Expression<Func<IWeb, object>>[] { w => w.WebTemplateConfiguration }
            };

            using (var context = await contextFactory.CreateAsync(new Uri(siteCollectionUrl), 
                                                                    new ExternalAuthenticationProvider((resourceUri, scopes) =>
                                                                    {
                                                                        return authenticationManager.GetAccessTokenAsync(scopes).GetAwaiter().GetResult();
                                                                    }),
                                                                    contextOptions))
            {
                webUrlsToScan.AddRange(await LoadAllWebsInSiteCollectionAsync(context));
            }

            Log.Information("Enumerated {Count} webs for scan {ScanId} with site collection {SiteCollectionUrl}", webUrlsToScan.Count, scanId, siteCollectionUrl);
#if DEBUG
            // Insert dummy webs
            if (options is TestOptions testOptions && webUrlsToScan.Count == 0)
            {
                // Add root web
                webUrlsToScan.Add(new EnumeratedWeb { WebUrl = "/", WebTemplate = "STS#0"});

                int numberOfWebs = new Random().Next(10);
                Log.Information("Number of webs to scan: {WebsToScan}", numberOfWebs + 1);

                for (int i = 0; i < numberOfWebs; i++)
                {
                    webUrlsToScan.Add(new EnumeratedWeb { WebUrl = $"/subsite{i}", WebTemplate = "STS#0" });
                }
            }
#endif

            return webUrlsToScan;
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


        private async Task<List<EnumeratedWeb>> LoadAllWebsInSiteCollectionAsync(PnPContext context)
        {
            List<EnumeratedWeb> webs = new();

            // Add the root web
            webs.Add(new EnumeratedWeb
            {
                WebUrl = "/",
                WebTemplate = $"{context.Web.WebTemplateConfiguration}"
            });

            // Get the sub webs from the root web of the site collection
            var enumeratedWebs = await context.GetSiteCollectionManager().GetSiteCollectionWebsWithDetailsAsync();       

            foreach(var enumeratedWeb in enumeratedWebs)
            {
                webs.Add(new EnumeratedWeb
                {
                    WebUrl = enumeratedWeb.ServerRelativeUrl,
                    WebTemplate = $"{enumeratedWeb.WebTemplateConfiguration}"
                });
            }

            return webs;
        }

    }
}
