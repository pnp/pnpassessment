using Microsoft.SharePoint.Client;
using Microsoft.SharePoint.Client.Search.Query;
using PnP.Core.Auth;
using PnP.Core.Services;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using Serilog;
using System.Diagnostics;

namespace PnP.Scanning.Core.Scanners
{
    internal abstract class ScannerBase
    {
        private static readonly Guid localSharePointResultsSourceId = new Guid("8413cd39-2156-4e00-b54d-11efd9abdb89");

        internal ScannerBase(ScanManager scanManager, StorageManager storageManager, IPnPContextFactory pnpContextFactory, Guid scanId, string siteUrl, string webUrl)
        {
            ScanManager = scanManager;
            StorageManager = storageManager;
            PnPContextFactory = pnpContextFactory;
            ScanId = scanId;
            SiteUrl = siteUrl;
            WebUrl = webUrl;
            Logger = Log.ForContext("ScanId", scanId);
        }

        internal string WebUrl { get; set; }

        internal string SiteUrl { get; set; }

        internal ScanManager ScanManager { get; private set; }

        internal StorageManager StorageManager { get; private set; }

        internal IPnPContextFactory PnPContextFactory { get; private set; }

        internal Guid ScanId { get; set; }

        internal ILogger Logger { get; private set; }

#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
        internal virtual async Task PreScanningAsync()
        {
        }

        internal virtual async Task PostScanningAsync()
        {
        }

        internal virtual async Task ExecuteAsync()
        {
        }
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously

        internal static ScannerBase? NewScanner(ScanManager scanManager, StorageManager storageManager, IPnPContextFactory pnpContextFactory, Guid scanId, string siteCollectionUrl, string webUrl, OptionsBase options)
        {
            // PER SCAN COMPONENT: instantiate the scan component here
            if (options is SyntexOptions syntexOptions)
            {
                return new SyntexScanner(scanManager, storageManager, pnpContextFactory, scanId, siteCollectionUrl, webUrl, syntexOptions);
            }
#if DEBUG
            else if (options is TestOptions testOptions)
            {
                return new TestScanner(scanManager, storageManager, pnpContextFactory, scanId, siteCollectionUrl, webUrl, testOptions);
            }
#endif

            return null;
        }

        protected async Task<PnPContext> GetPnPContextAsync()
        {
            return await GetPnPContextImplementationAsync(null);
        }

        protected async Task<PnPContext> GetPnPContextAsync(PnPContextOptions contextOptions)
        {
            return await GetPnPContextImplementationAsync(contextOptions);
        }

        private async Task<PnPContext> GetPnPContextImplementationAsync(PnPContextOptions? contextOptions)
        {
            if (contextOptions != null)
            {
                if (contextOptions.Properties == null)
                {
                    contextOptions.Properties = new Dictionary<string, object>();
                }

                contextOptions.Properties[Constants.PnPContextPropertyScanId] = ScanId;
            }
            else
            {
                contextOptions = new PnPContextOptions
                {
                    Properties = new Dictionary<string, object>() { { Constants.PnPContextPropertyScanId, ScanId } }
                };
            }

            return await PnPContextFactory.CreateAsync(new Uri($"{SiteUrl}{WebUrl}"),
                                                       new ExternalAuthenticationProvider((resourceUri, scopes) =>
                                                       {
                                                           return ScanManager.GetScanAuthenticationManager(ScanId).GetAccessTokenAsync(scopes).GetAwaiter().GetResult();
                                                       }),
                                                       contextOptions);
        }

        protected ClientContext GetClientContext()
        {
            var clientContext = new ClientContext(new Uri($"{SiteUrl}{WebUrl}"))
            {
                DisableReturnValueCache = true
            };

            clientContext.ExecutingWebRequest += (sender, args) =>
            {
                var uri = new Uri($"{SiteUrl}{WebUrl}");
                var scopes = new[] { $"{uri.Scheme}://{uri.Authority}/.default" };

                string accessToken = ScanManager.GetScanAuthenticationManager(ScanId).GetAccessTokenAsync(scopes).GetAwaiter().GetResult();
                args.WebRequestExecutor.RequestHeaders["Authorization"] = "Bearer " + accessToken;
            };

            return clientContext;
        }

        protected void AddToCache(string key, string value)
        {
            key = BuildKey(key);
            if (ScanManager.Cache.ContainsKey(key))
            {
                ScanManager.Cache[key] = value;
                Logger.Information("Cache key {Key} was updated with value {Value}", key, value);
            }
            else
            {
                if (ScanManager.Cache.TryAdd(key, value))
                {
                    Logger.Information("Key {Key} was added to cache with value {Value}", key, value);
                }
                else
                {
                    Logger.Warning("Adding key {Key} with value {Value} failed", key, value);
                }
            }
        }

        protected string GetFromCache(string key)
        {
            key = BuildKey(key);

            if (ScanManager.Cache.TryGetValue(key, out var value))
            {
                return value;
            }
            else
            {
                Logger.Warning("The value for key {Key} with was requested but not found in cache", key);
                return null;
            }
        }

        private string BuildKey(string key)
        {
            key = key.Trim().Replace(" ", "-");

            if (string.IsNullOrEmpty(key))
            {
                Logger.Error("Empty cache key presented");
                throw new Exception($"Empty cache key presented for scan {ScanId}");
            }

            return $"{ScanId}-{key}";
        }

        internal async Task<List<Dictionary<string, string>>> SearchAsync(Microsoft.SharePoint.Client.Web web, string keywordQueryValue, List<string> propertiesToRetrieve, bool trimDuplicates = false, bool singleResult = false)
        {
            try
            {
                int maxSearchTime = 5 * 60 * 1000; // 5 minutes
                Stopwatch stopwatch = new();

                List<Dictionary<string, string>> searchResults = new();

                stopwatch.Start();
                var keywordQuery = new KeywordQuery(web.Context);
                keywordQuery.TrimDuplicates = trimDuplicates;
                keywordQuery.SourceId = localSharePointResultsSourceId;

                //property IndexDocId is required, so add it if not yet present
                if (!propertiesToRetrieve.Contains("IndexDocId"))
                {
                    propertiesToRetrieve.Add("IndexDocId");
                }

                int totalRows = 0;

                Logger.Information("Start search query {KeywordQueryValue}", keywordQueryValue);
                totalRows = await ProcessQueryAsync(web, keywordQueryValue, propertiesToRetrieve, searchResults, keywordQuery);
                Logger.Information("Found {TotalRows} rows...", totalRows);

                if (singleResult)
                {
                    // No point in trying to get into a search loop as there's only 1 result
                    return searchResults;
                }

                if (totalRows > 0)
                {
                    string previousLastIndexDocId = null;

                    while (totalRows > 0)
                    {
                        if (searchResults.Last().TryGetValue("IndexDocId", out string lastIndexDocIdString))
                        {
                            // Leave the loop if for some reason we're getting the same lastIndexDocId --> should not happen, this is an 
                            // extra safety to prevent from getting stuck in an infinite loop.
                            if (previousLastIndexDocId != null && previousLastIndexDocId.Equals(lastIndexDocIdString))
                            {
                                Logger.Warning("Breaking loop, lastIndexDocId was {PreviousLastIndexDocId}", previousLastIndexDocId);
                                break;
                            }

                            Logger.Information($"Retrieving a batch of up to 500 search results");
                            keywordQuery.SourceId = localSharePointResultsSourceId;
                            totalRows = await ProcessQueryAsync(web, keywordQueryValue + " AND IndexDocId >" + lastIndexDocIdString, propertiesToRetrieve, searchResults, keywordQuery);
                            // From the second Query get the next set (rowlimit) of search result based on IndexDocId
                            previousLastIndexDocId = lastIndexDocIdString;
                        }

                        // Safetime measure to prevent endless looping to load search results...
                        if (stopwatch.ElapsedMilliseconds > maxSearchTime)
                        {
                            Logger.Warning("Breaking search loop as we exceeded the max time of {MaxSearchTime} milliseconds", maxSearchTime);
                            return searchResults;
                        }
                    }
                }

                return searchResults;
            }
            catch (Exception)
            {
                // rethrow does lose one line of stack trace, but we want to log the error at the component boundary
                throw;
            }
        }

        private async Task<int> ProcessQueryAsync(Microsoft.SharePoint.Client.Web web, string keywordQueryValue, List<string> propertiesToRetrieve, List<Dictionary<string, string>> sites, KeywordQuery keywordQuery)
        {
            int totalRows = 0;
            keywordQuery.QueryText = keywordQueryValue;
            keywordQuery.RowLimit = 500;

            // Make the query return the requested properties
            foreach (var property in propertiesToRetrieve)
            {
                keywordQuery.SelectProperties.Add(property);
            }

            // Ensure sorting is done on IndexDocId to allow for performant paging
            keywordQuery.SortList.Add("IndexDocId", SortDirection.Ascending);

            var searchExec = new SearchExecutor(web.Context);

            // Important to avoid trimming "similar" site collections
            keywordQuery.TrimDuplicates = false;

            ClientResult<ResultTableCollection> results = searchExec.ExecuteQuery(keywordQuery);
            await web.Context.ExecuteQueryRetryAsync(Logger);

            if (results != null)
            {
                if (results.Value[0].RowCount > 0)
                {
                    totalRows = results.Value[0].TotalRows;

                    foreach (var row in results.Value[0].ResultRows)
                    {
                        Dictionary<string, string> item = new Dictionary<string, string>();

                        foreach (var property in propertiesToRetrieve)
                        {
                            if (row[property] != null)
                            {
                                item.Add(property, row[property].ToString());
                            }
                            else
                            {
                                item.Add(property, "");
                            }
                        }
                        sites.Add(item);
                    }
                }
            }

            return totalRows;
        }
    }
}
