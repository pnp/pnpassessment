using PnP.Core.Auth;
using PnP.Core.Services;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using Serilog;

namespace PnP.Scanning.Core.Scanners
{
    internal abstract class ScannerBase
    {
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
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
        {
        }

#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
        internal virtual async Task ExecuteAsync()
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
        {
        }

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
    }
}
