using PnP.Core.Model;
using PnP.Core.QueryModel;
using PnP.Core.Model.SharePoint;
using PnP.Core.Services;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using System.Linq.Expressions;

namespace PnP.Scanning.Core.Scanners
{
    internal class WorkflowScanner : ScannerBase
    {
        public WorkflowScanner(ScanManager scanManager, StorageManager storageManager, IPnPContextFactory pnpContextFactory,
                               Guid scanId, string siteUrl, string webUrl, WorkflowOptions options) :
                               base(scanManager, storageManager, pnpContextFactory, scanId, siteUrl, webUrl)
        {
            Options = options;
        }

        internal WorkflowOptions Options { get; set; }

        internal async override Task ExecuteAsync()
        {
            Logger.Information("Starting Workflow assessment of web {SiteUrl}{WebUrl}", SiteUrl, WebUrl);

            // Define extra Web/Site data that we want to load when the context is inialized
            // This will not require extra server roundtrips
            PnPContextOptions options = new()
            {
                AdditionalSitePropertiesOnCreate = new Expression<Func<ISite, object>>[]
                {
                    w => w.RootWeb.QueryProperties(p => p.ContentTypes.QueryProperties(p => p.StringId, p => p.Name))
                },
                AdditionalWebPropertiesOnCreate = new Expression<Func<IWeb, object>>[]
                {
                    w => w.Lists.QueryProperties(r => r.Title,
                                                 r => r.RootFolder.QueryProperties(f => f.ServerRelativeUrl))
                }
            };

            using (var context = await GetPnPContextAsync(options))
            using (var csomContext = GetClientContext(context))
            {
                // Call the workflow scan component
                await WorkflowScanComponent.ExecuteAsync(Options, this, context, csomContext).ConfigureAwait(false);
            }

            Logger.Information("Workflow assessment of web {SiteUrl}{WebUrl} done", SiteUrl, WebUrl);
        }

#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
        internal async override Task PreScanningAsync()
        {
            Logger.Information("Pre assessment work is starting");

            await SendRequestWithClientTagAsync();

            WorkflowManager.Instance.LoadWorkflowDefaultActions();

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
