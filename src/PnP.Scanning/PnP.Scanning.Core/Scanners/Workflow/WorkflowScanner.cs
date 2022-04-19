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
        private class WorkflowInstanceCounts
        {
            public int Total { get; set; }

            public int Started { get; set; }

            public int NotStarted { get; set; }

            public int Cancelled { get; set; }

            public int Cancelling { get; set; }

            public int Suspended { get; set; }

            public int Terminated { get; set; }

            public int Completed { get; set; }
        }

        public WorkflowScanner(ScanManager scanManager, StorageManager storageManager, IPnPContextFactory pnpContextFactory,
                               CsomEventHub csomEventHub, Guid scanId, string siteUrl, string webUrl, WorkflowOptions options) :
                               base(scanManager, storageManager, pnpContextFactory, csomEventHub, scanId, siteUrl, webUrl)
        {
            Options = options;
        }

        internal WorkflowOptions Options { get; set; }


        internal async override Task ExecuteAsync()
        {
            Logger.Information("Starting Workflow assessment of web {SiteUrl}{WebUrl}", SiteUrl, WebUrl);

            List<Workflow> workflowLists = new();
            WorkflowDefinition[] siteDefinitions = null;
            WorkflowSubscription[] siteSubscriptions = null;

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
            using (var csomContext = GetClientContext())
            {
                Microsoft.SharePoint.Client.Web web = csomContext.Web;

                WorkflowInstanceService instanceService = null;

                try
                {
                    var servicesManager = new WorkflowServicesManager(web.Context, web);
                    var deploymentService = servicesManager.GetWorkflowDeploymentService();
                    instanceService = servicesManager.GetWorkflowInstanceService();
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
                    Logger.Error(ex, $"No workflow service present!");
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
                                // Count the workflow instances for this subscription
                                var instanceCounts = await GetInstanceCountsAsync(web.Context, instanceService, siteWorkflowSubscription);

                                workflowLists.Add(new Workflow
                                {
                                    ScanId = ScanId,
                                    SiteUrl = SiteUrl,
                                    WebUrl = WebUrl,
                                    ListTitle = "",
                                    ListUrl = "",
                                    ListId = Guid.Empty,
                                    ContentTypeId = "",
                                    ContentTypeName = "",
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
                                    TotalInstances = instanceCounts.Total,
                                    CancelledInstances = instanceCounts.Cancelled,
                                    CancellingInstances = instanceCounts.Cancelling,
                                    TerminatedInstances = instanceCounts.Terminated,
                                    StartedInstances = instanceCounts.Started,
                                    NotStartedInstances = instanceCounts.NotStarted,
                                    CompletedInstances = instanceCounts.Completed,
                                    SuspendedInstances = instanceCounts.Suspended,
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
                                ContentTypeId = "",
                                ContentTypeName = "",
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
                                // Find associated list
                                Guid associatedListId = Guid.Empty;
                                string associatedListTitle = "";
                                string associatedListUrl = "";
                                if (Guid.TryParse(GetWorkflowProperty(listWorkflowSubscription, "Microsoft.SharePoint.ActivationProperties.ListId"), out Guid associatedListIdValue))
                                {
                                    associatedListId = associatedListIdValue;

                                    var listLookup = context.Web.Lists.AsRequested().FirstOrDefault(p => p.Id.Equals(associatedListId));
                                    if (listLookup != null)
                                    {
                                        associatedListTitle = listLookup.Title;
                                        associatedListUrl = listLookup.RootFolder.ServerRelativeUrl;
                                    }
                                }

                                // Find associated content type
                                string associatedContentTypeId = "";
                                string associatedContentTypeName = "";
                                if (!string.IsNullOrEmpty(listWorkflowSubscription.ParentContentTypeId))
                                {
                                    var contentType = context.Site.RootWeb.ContentTypes.AsRequested().FirstOrDefault(p => p.StringId == listWorkflowSubscription.ParentContentTypeId);
                                    if (contentType != null)
                                    {
                                        associatedContentTypeId = contentType.StringId;
                                        associatedContentTypeName = contentType.Name;
                                    }
                                }

                                // Count the workflow instances for this subscription
                                var instanceCounts = await GetInstanceCountsAsync(web.Context, instanceService, listWorkflowSubscription);

                                workflowLists.Add(new Workflow
                                {
                                    ScanId = ScanId,
                                    SiteUrl = SiteUrl,
                                    WebUrl = WebUrl,
                                    ListTitle = associatedListTitle,
                                    ListUrl = associatedListUrl,
                                    ListId = associatedListId,
                                    ContentTypeId = associatedContentTypeId,
                                    ContentTypeName = associatedContentTypeName,
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
                                    TotalInstances = instanceCounts.Total,
                                    CancelledInstances = instanceCounts.Cancelled,
                                    CancellingInstances = instanceCounts.Cancelling,
                                    TerminatedInstances = instanceCounts.Terminated,
                                    StartedInstances = instanceCounts.Started,
                                    NotStartedInstances = instanceCounts.NotStarted,
                                    CompletedInstances = instanceCounts.Completed,
                                    SuspendedInstances = instanceCounts.Suspended,
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
                                ContentTypeId = "",
                                ContentTypeName = "",
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

        private static async Task<WorkflowInstanceCounts> GetInstanceCountsAsync(ClientRuntimeContext clientContext, WorkflowInstanceService instanceService, WorkflowSubscription subscription)
        {
            WorkflowInstanceCounts instanceCounts = new();

            ClientResult<int> total = instanceService.CountInstances(subscription);
            ClientResult<int> started = instanceService.CountInstancesWithStatus(subscription, WorkflowStatus.Started);
            ClientResult<int> notStarted = instanceService.CountInstancesWithStatus(subscription, WorkflowStatus.NotStarted);
            ClientResult<int> cancelled = instanceService.CountInstancesWithStatus(subscription, WorkflowStatus.Canceled);
            ClientResult<int> cancelling = instanceService.CountInstancesWithStatus(subscription, WorkflowStatus.Canceling);
            ClientResult<int> suspended = instanceService.CountInstancesWithStatus(subscription, WorkflowStatus.Suspended);
            ClientResult<int> terminated = instanceService.CountInstancesWithStatus(subscription, WorkflowStatus.Terminated);
            ClientResult<int> completed = instanceService.CountInstancesWithStatus(subscription, WorkflowStatus.Completed);

            await clientContext.ExecuteQueryRetryAsync();

            instanceCounts.Total = total.Value;
            instanceCounts.Started = started.Value;
            instanceCounts.NotStarted = notStarted.Value;
            instanceCounts.Cancelled = cancelled.Value;
            instanceCounts.Cancelling = cancelling.Value;
            instanceCounts.Suspended = suspended.Value;
            instanceCounts.Terminated = terminated.Value;
            instanceCounts.Completed = completed.Value;

            return instanceCounts;
        }

        private static bool GetWorkflowPropertyBool(IDictionary<string, string> properties, string property)
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

        private static DateTime GetWorkflowPropertyDateTime(IDictionary<string, string> properties, string property)
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

        private static string GetWorkflowProperty(WorkflowSubscription subscription, string propertyName)
        {
            if (subscription.PropertyDefinitions.ContainsKey(propertyName))
            {
                return subscription.PropertyDefinitions[propertyName];
            }

            return "";
        }
    }
}
