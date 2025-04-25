using PnP.Core.Admin.Model.SharePoint;
using PnP.Core.Model;
using PnP.Core.Model.SharePoint;
using PnP.Core.QueryModel;
using PnP.Core.Services;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using Serilog;
using System.Linq.Expressions;

namespace PnP.Scanning.Core.Scanners
{
    internal class AlertsScanner: ScannerBase
    {
        public AlertsScanner(ScanManager scanManager, StorageManager storageManager, IPnPContextFactory pnpContextFactory,
                               Guid scanId, string siteUrl, string webUrl, string webTemplate, AlertsOptions options,
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

        internal AlertsOptions Options { get; set; }

        internal VanityUrlOptions VanityUrlOptions { get; private set; }

        internal async override Task ExecuteAsync()
        {
            Logger.Information("Starting Alerts assessment of web {SiteUrl}{WebUrl}", SiteUrl, WebUrl);

            // Define extra Web/Site data that we want to load when the context is inialized
            // This will not require extra server roundtrips
            PnPContextOptions options = new()
            {
                AdditionalWebPropertiesOnCreate = new Expression<Func<IWeb, object>>[]
                {
                    p => p.ServerRelativeUrl,
                    p => p.Alerts.QueryProperties(p => p.Id,
                                                  p => p.Title,
                                                  p => p.AlertType,
                                                  p => p.EventType,
                                                  p => p.AlertTemplateName,
                                                  p => p.Status,
                                                  p => p.AlertFrequency,
                                                  p => p.DeliveryChannels,
                                                  p => p.Filter,
                                                  p => p.ListId,
                                                  p => p.ListUrl,
                                                  p => p.AllProperties,
                                                  p => p.List.QueryProperties(p => p.Title),
                                                  p => p.User.QueryProperties(p => p.PrincipalType, p => p.LoginName, p => p.Title, p => p.Mail))
                }
            };

            using (var context = await GetPnPContextAsync(options))
            {
                // Call the Alerts scan component
                await AlertsScanComponent.ExecuteAsync(this, context, null, VanityUrlOptions).ConfigureAwait(false);
            }

            Logger.Information("Alerts assessment of web {SiteUrl}{WebUrl} done", SiteUrl, WebUrl);
        }

#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
        internal async override Task PreScanningAsync()
        {
            Logger.Information("Pre assessment work is starting");

            await SendRequestWithClientTagAsync();

            Logger.Information("Pre assessment work done");
        }

        internal async override Task PostScanningAsync()
        {
            Logger.Information("Post assessment work is starting");

            Logger.Information("Post assessment work done");
        }
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously

    }
}
