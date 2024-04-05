using PnP.Core.Admin.Model.SharePoint;
using PnP.Core.Model.SharePoint;
using PnP.Core.Services;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using Serilog;
using System.Linq.Expressions;

namespace PnP.Scanning.Core.Scanners
{
    /// <summary>
    /// Minimal permission scopes needed:
    /// Application permissions: Graph => Sites.Read.All, Application.Read.All SharePoint => Sites.Read.All
    /// Delegated permissions: Graph => Sites.Read.All, User.Read, Application.Read.All SharePoint => AllSites.Read
    /// </summary>
    internal class AddInACSScanner : ScannerBase
    {
        public AddInACSScanner(ScanManager scanManager, StorageManager storageManager, IPnPContextFactory pnpContextFactory,
                               Guid scanId, string siteUrl, string webUrl, string webTemplate, AddInACSOptions options,
                               string adminCenterUrl, string mySiteHostUrl) :
                               base(scanManager, storageManager, pnpContextFactory, scanId, siteUrl, webUrl, webTemplate)
        {
            Options = options;

            if (!string.IsNullOrEmpty(mySiteHostUrl) && !string.IsNullOrEmpty(adminCenterUrl))
            {
                VanityUrlOptions = new VanityUrlOptions
                {
                    AdminCenterUri = new Uri(adminCenterUrl),
                    MySiteHostUri = new Uri(mySiteHostUrl)
                };
            }
        }

        internal AddInACSOptions Options { get; set; }

        internal VanityUrlOptions VanityUrlOptions { get; private set; }

        internal async override Task ExecuteAsync()
        {
            Logger.Information("Starting SharePoint Add-In and Azure ACS assessment of web {SiteUrl}{WebUrl}", SiteUrl, WebUrl);

            // Define extra Web/Site data that we want to load when the context is inialized
            // This will not require extra server roundtrips
            PnPContextOptions options = new()
            {
                AdditionalWebPropertiesOnCreate = new Expression<Func<IWeb, object>>[]
                {
                    w => w.ServerRelativeUrl
                }
            };

            using (var context = await GetPnPContextAsync(options))
            {
                // Call the SharePoint Add-In and Azure ACS scan component
                await AddInACSScanComponent.ExecuteAsync(this, context, null, VanityUrlOptions).ConfigureAwait(false);
            }

            Logger.Information("SharePoint Add-In and Azure ACS assessment of web {SiteUrl}{WebUrl} done", SiteUrl, WebUrl);
        }

#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
        internal async override Task PreScanningAsync()
        {
            Logger.Information("Pre assessment work is starting");

            try
            {
                await SendRequestWithClientTagAsync();

                using (var context = await GetPnPContextAsync())
                {
                    await AddInACSScanComponent.ExecutePreScanningAsync(this, context, null, VanityUrlOptions).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                // Add extra error message to clarify the possibilty of a locked site when debugging future failures
                Logger.Error(ex, "Code in PreScanningAsync failed. This can happen when the first enumerated site collection is locked or when the used Entra app was not granted the correct permissions.");
                throw;
            }

            Logger.Information("Pre assessment work done");
        }

        internal async override Task PostScanningAsync()
        {
            Logger.Information("Post assessment work is starting");

            await AddInACSScanComponent.ExecutePostScanningAsync(this, null, null).ConfigureAwait(false);

            Logger.Information("Post assessment work done");
        }
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
    }
}
