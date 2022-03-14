using Microsoft.SharePoint.Client;
using Microsoft.SharePoint.Client.WorkflowServices;
using PnP.Core.Model;
using PnP.Core.Model.SharePoint;
using PnP.Core.QueryModel;
using PnP.Core.Services;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using System.Globalization;
using System.Linq.Expressions;

namespace PnP.Scanning.Core.Scanners
{
    internal class WorkflowScanner : ScannerBase
    {
        private static readonly string[] OOBWorkflowIDStarts = new string[]
        {
            "e43856d2-1bb4-40ef-b08b-016d89a00",    // Publishing approval
            "3bfb07cb-5c6a-4266-849b-8d6711700",    // Collect feedback - 2010
            "46c389a4-6e18-476c-aa17-289b0c79fb8f", // Collect feedback
            "77c71f43-f403-484b-bcb2-303710e00",    // Collect signatures - 2010
            "2f213931-3b93-4f81-b021-3022434a3114", // Collect signatures
            "8ad4d8f0-93a7-4941-9657-cf3706f00",    // Approval - 2010
            "b4154df4-cc53-4c4f-adef-1ecf0b7417f6", // Translation management
            "c6964bff-bf8d-41ac-ad5e-b61ec111731a", // Three state
            "c6964bff-bf8d-41ac-ad5e-b61ec111731c", // Approval
            "dd19a800-37c1-43c0-816d-f8eb5f4a4145", // Disposition approval
        };

        public WorkflowScanner(ScanManager scanManager, StorageManager storageManager, IPnPContextFactory pnpContextFactory,
                               CsomEventHub csomEventHub, Guid scanId, string siteUrl, string webUrl, WorkflowOptions options) :
                               base(scanManager, storageManager, pnpContextFactory, csomEventHub, scanId, siteUrl, webUrl)
        {
            Options = options;
        }

        internal WorkflowOptions Options { get; set; }


        internal async override Task ExecuteAsync()
        {
            Logger.Information("Starting Workflow scan of web {SiteUrl}{WebUrl}", SiteUrl, WebUrl);

            List<Workflow> workflowLists = new();
            WorkflowDefinition[] siteDefinitions = null;
            WorkflowSubscription[] siteSubscriptions = null;

            // Define extra Web/Site data that we want to load when the context is inialized
            // This will not require extra server roundtrips
            PnPContextOptions options = new()
            {
                AdditionalWebPropertiesOnCreate = new Expression<Func<IWeb, object>>[]
                {
                    w => w.Lists.QueryProperties(r => r.Title, r => r.RootFolder.QueryProperties(f => f.ServerRelativeUrl))
                }
            };

            using (var context = await GetPnPContextAsync(options))
            using (var csomContext = GetClientContext())
            {
                Microsoft.SharePoint.Client.Web web = csomContext.Web;

                try
                {
                    var servicesManager = new WorkflowServicesManager(web.Context, web);
                    var deploymentService = servicesManager.GetWorkflowDeploymentService();
                    var subscriptionService = servicesManager.GetWorkflowSubscriptionService();

                    var definitions = deploymentService.EnumerateDefinitions(false);
                    web.Context.Load(definitions);

                    var subscriptions = subscriptionService.EnumerateSubscriptions();
                    web.Context.Load(subscriptions);

                    await web.Context.ExecuteQueryRetryAsync();

                    siteDefinitions = definitions.ToArray();
                    siteSubscriptions = subscriptions.ToArray();
                }
                catch (ServerException ex)
                {
                    // If there is no workflow service present in the farm this method will throw an error. 
                    // Swallow the exception
                }

                // We've found SP2013 site scoped workflows
                if (siteDefinitions != null && siteDefinitions.Length > 0)
                {
                    foreach (var siteDefinition in siteDefinitions.Where(p => p.RestrictToType != null && (p.RestrictToType.Equals("site", StringComparison.InvariantCultureIgnoreCase) || p.RestrictToType.Equals("universal", StringComparison.InvariantCultureIgnoreCase))))
                    {
                        // Check if this workflow is also in use
                        var siteWorkflowSubscriptions = siteSubscriptions.Where(p => p.DefinitionId.Equals(siteDefinition.Id));

                        // Perform workflow analysis
                        WorkflowActionAnalysis workFlowAnalysisResult = null;
                        WorkflowTriggerAnalysis workFlowTriggerAnalysisResult = null;
                        if (Options.Analyze)
                        {
                            workFlowAnalysisResult = WorkflowManager.Instance.ParseWorkflowDefinition(siteDefinition.Xaml);
                            workFlowTriggerAnalysisResult = WorkflowManager.ParseWorkflowTriggers(GetWorkflowPropertyBool(siteDefinition.Properties, "SPDConfig.StartOnCreate"),
                                                                                                  GetWorkflowPropertyBool(siteDefinition.Properties, "SPDConfig.StartOnChange"),
                                                                                                  GetWorkflowPropertyBool(siteDefinition.Properties, "SPDConfig.StartManually"));
                        }

                        if (siteWorkflowSubscriptions.Any())
                        {
                            foreach (var siteWorkflowSubscription in siteWorkflowSubscriptions)
                            {
                                workflowLists.Add(new Workflow
                                {
                                    ScanId = ScanId,
                                    SiteUrl = SiteUrl,
                                    WebUrl = WebUrl,
                                    ListTitle = "",
                                    ListUrl = "",
                                    Scope = "Site",
                                    RestrictToType = siteDefinition.RestrictToType,
                                    DefinitionName = siteDefinition.DisplayName,
                                    DefinitionDescription = siteDefinition.Description,
                                    SubscriptionName = siteWorkflowSubscription.Name,
                                    HasSubscriptions = true,
                                    Enabled = siteWorkflowSubscription.Enabled,
                                    DefinitionId = siteDefinition.Id,
                                    IsOOBWorkflow = false,
                                    SubscriptionId = siteWorkflowSubscription.Id,
                                    UsedActions = string.Join(",", workFlowAnalysisResult?.WorkflowActions),
                                    ActionCount = workFlowAnalysisResult != null ? workFlowAnalysisResult.ActionCount : 0,
                                    UsedTriggers = string.Join(",", workFlowTriggerAnalysisResult?.WorkflowTriggers),
                                    UnsupportedActionsInFlow = string.Join(",", workFlowAnalysisResult?.UnsupportedActions),
                                    UnsupportedActionCount = workFlowAnalysisResult != null ? workFlowAnalysisResult.UnsupportedAccountCount : 0,
                                    LastDefinitionEdit = GetWorkflowPropertyDateTime(siteDefinition.Properties, "Definition.ModifiedDateUTC"),
                                    LastSubscriptionEdit = GetWorkflowPropertyDateTime(siteWorkflowSubscription.PropertyDefinitions, "SharePointWorkflowContext.Subscription.ModifiedDateUTC"),
                                });

                            }
                        }
                        else
                        {
                            workflowLists.Add(new Workflow
                            {
                                ScanId = ScanId,
                                SiteUrl = SiteUrl,
                                WebUrl = WebUrl,
                                ListTitle = "",
                                ListUrl = "",
                                Scope = "Site",
                                RestrictToType = siteDefinition.RestrictToType,
                                DefinitionName = siteDefinition.DisplayName,
                                DefinitionDescription = siteDefinition.Description,
                                SubscriptionName = "",
                                HasSubscriptions = false,
                                Enabled = false,
                                DefinitionId = siteDefinition.Id,
                                IsOOBWorkflow = false,
                                SubscriptionId = Guid.Empty,
                                UsedActions = string.Join(",", workFlowAnalysisResult?.WorkflowActions),
                                ActionCount = workFlowAnalysisResult != null ? workFlowAnalysisResult.ActionCount : 0,
                                UnsupportedActionsInFlow = string.Join(",", workFlowAnalysisResult?.UnsupportedActions),
                                UnsupportedActionCount = workFlowAnalysisResult != null ? workFlowAnalysisResult.UnsupportedAccountCount : 0,
                                UsedTriggers = string.Join(",", workFlowTriggerAnalysisResult?.WorkflowTriggers),
                                LastDefinitionEdit = GetWorkflowPropertyDateTime(siteDefinition.Properties, "Definition.ModifiedDateUTC"),
                            });

                        }
                    }
                }

                // We've found SP2013 list scoped workflows
                if (siteDefinitions != null && siteDefinitions.Length > 0)
                {
                    foreach (var listDefinition in siteDefinitions.Where(p => p.RestrictToType != null && (p.RestrictToType.Equals("list", StringComparison.InvariantCultureIgnoreCase) || p.RestrictToType.Equals("universal", StringComparison.InvariantCultureIgnoreCase))))
                    {
                        // Check if this workflow is also in use
                        var listWorkflowSubscriptions = siteSubscriptions.Where(p => p.DefinitionId.Equals(listDefinition.Id));

                        // Perform workflow analysis
                        WorkflowActionAnalysis workFlowAnalysisResult = null;
                        WorkflowTriggerAnalysis workFlowTriggerAnalysisResult = null;
                        if (Options.Analyze)
                        {
                            workFlowAnalysisResult = WorkflowManager.Instance.ParseWorkflowDefinition(listDefinition.Xaml);
                            workFlowTriggerAnalysisResult = WorkflowManager.ParseWorkflowTriggers(GetWorkflowPropertyBool(listDefinition.Properties, "SPDConfig.StartOnCreate"), 
                                                                                                  GetWorkflowPropertyBool(listDefinition.Properties, "SPDConfig.StartOnChange"), 
                                                                                                  GetWorkflowPropertyBool(listDefinition.Properties, "SPDConfig.StartManually"));
                        }

                        if (listWorkflowSubscriptions.Any())
                        {
                            foreach (var listWorkflowSubscription in listWorkflowSubscriptions)
                            {
                                Guid associatedListId = Guid.Empty;
                                string associatedListTitle = "";
                                string associatedListUrl = "";
                                if (Guid.TryParse(GetWorkflowProperty(listWorkflowSubscription, "Microsoft.SharePoint.ActivationProperties.ListId"), out Guid associatedListIdValue))
                                {
                                    associatedListId = associatedListIdValue;

                                    // Lookup this list and update title and url
                                    var listLookup = context.Web.Lists.AsRequested().Where(p => p.Id.Equals(associatedListId)).FirstOrDefault();
                                    if (listLookup != null)
                                    {
                                        associatedListTitle = listLookup.Title;
                                        associatedListUrl = listLookup.RootFolder.ServerRelativeUrl;
                                    }
                                }

                                workflowLists.Add(new Workflow
                                {
                                    ScanId = ScanId,
                                    SiteUrl = SiteUrl,
                                    WebUrl = WebUrl,
                                    ListTitle = associatedListTitle,
                                    ListUrl = associatedListUrl,
                                    ListId = associatedListId,
                                    Scope = "List",
                                    RestrictToType = listDefinition.RestrictToType,
                                    DefinitionName = listDefinition.DisplayName,
                                    DefinitionDescription = listDefinition.Description,
                                    SubscriptionName = listWorkflowSubscription.Name,
                                    HasSubscriptions = true,
                                    Enabled = listWorkflowSubscription.Enabled,
                                    DefinitionId = listDefinition.Id,
                                    IsOOBWorkflow = false,
                                    SubscriptionId = listWorkflowSubscription.Id,
                                    UsedActions = string.Join(",", workFlowAnalysisResult?.WorkflowActions),
                                    ActionCount = workFlowAnalysisResult != null ? workFlowAnalysisResult.ActionCount : 0,
                                    UsedTriggers = string.Join(",", workFlowTriggerAnalysisResult?.WorkflowTriggers),
                                    UnsupportedActionsInFlow = string.Join(",", workFlowAnalysisResult?.UnsupportedActions),
                                    UnsupportedActionCount = workFlowAnalysisResult != null ? workFlowAnalysisResult.UnsupportedAccountCount : 0,
                                    LastDefinitionEdit = GetWorkflowPropertyDateTime(listDefinition.Properties, "Definition.ModifiedDateUTC"),
                                    LastSubscriptionEdit = GetWorkflowPropertyDateTime(listWorkflowSubscription.PropertyDefinitions, "SharePointWorkflowContext.Subscription.ModifiedDateUTC"),
                                });
                            }
                        }
                        else
                        {
                            workflowLists.Add(new Workflow
                            {
                                ScanId = ScanId,
                                SiteUrl = SiteUrl,
                                WebUrl = WebUrl,
                                ListTitle = "",
                                ListUrl = "",
                                ListId = Guid.Empty,
                                Scope = "List",
                                RestrictToType = listDefinition.RestrictToType,
                                DefinitionName = listDefinition.DisplayName,
                                DefinitionDescription = listDefinition.Description,
                                SubscriptionName = "",
                                HasSubscriptions = false,
                                Enabled = false,
                                DefinitionId = listDefinition.Id,
                                IsOOBWorkflow = false,
                                SubscriptionId = Guid.Empty,
                                UsedActions = string.Join(",", workFlowAnalysisResult?.WorkflowActions),
                                ActionCount = workFlowAnalysisResult != null ? workFlowAnalysisResult.ActionCount : 0,
                                UsedTriggers = string.Join(",", workFlowTriggerAnalysisResult?.WorkflowTriggers),
                                UnsupportedActionsInFlow = string.Join(",", workFlowAnalysisResult?.UnsupportedActions),
                                UnsupportedActionCount = workFlowAnalysisResult != null ? workFlowAnalysisResult.UnsupportedAccountCount : 0,
                                LastDefinitionEdit = GetWorkflowPropertyDateTime(listDefinition.Properties, "Definition.ModifiedDateUTC"),
                            });
                        }
                    }
                }

                if (workflowLists.Count > 0)
                {
                    await StorageManager.StoreWorkflowInformationAsync(ScanId, workflowLists);
                }

            }

            Logger.Information("Workflow scan of web {SiteUrl}{WebUrl} done", SiteUrl, WebUrl);
        }

        internal async override Task PreScanningAsync()
        {
            Logger.Information("Pre scanning work is starting");

            WorkflowManager.Instance.LoadWorkflowDefaultActions();

            Logger.Information("Pre scanning work done");
        }

        internal async override Task PostScanningAsync()
        {
            Logger.Information("Post scanning work is starting");

            Logger.Information("Post scanning work done");
        }

        private bool GetWorkflowPropertyBool(IDictionary<string, string> properties, string property)
        {
            if (string.IsNullOrEmpty(property) || properties == null)
            {
                return false;
            }

            if (properties.ContainsKey(property))
            {
                if (bool.TryParse(properties[property], out bool parsedValue))
                {
                    return parsedValue;
                }
            }

            return false;
        }

        private DateTime GetWorkflowPropertyDateTime(IDictionary<string, string> properties, string property)
        {
            if (string.IsNullOrEmpty(property) || properties == null)
            {
                return DateTime.MinValue;
            }

            if (properties.ContainsKey(property))
            {
                if (DateTime.TryParseExact(properties[property], "M/d/yyyy h:m:s tt", new CultureInfo("en-US"), DateTimeStyles.AssumeUniversal, out DateTime parsedValue))
                {
                    return parsedValue;
                }
            }

            return DateTime.MinValue;
        }

        private string GetWorkflowProperty(WorkflowSubscription subscription, string propertyName)
        {
            if (subscription.PropertyDefinitions.ContainsKey(propertyName))
            {
                return subscription.PropertyDefinitions[propertyName];
            }

            return "";
        }
    }
}
