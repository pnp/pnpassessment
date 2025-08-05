using Microsoft.SharePoint.Client;
using Microsoft.SharePoint.Client.WorkflowServices;
using PnP.Core.QueryModel;
using PnP.Core.Services;
using PnP.Scanning.Core.Storage;
using System.Globalization;

namespace PnP.Scanning.Core.Scanners
{
    internal static class WorkflowScanComponent
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

        internal static async Task ExecuteAsync(WorkflowOptions workflowOptions, ScannerBase scannerBase, PnPContext context, ClientContext csomContext)
        {
            List<Workflow> workflowLists = new();
            WorkflowDefinition[] siteDefinitions = null;
            WorkflowSubscription[] siteSubscriptions = null;

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

                await web.Context.ExecuteQueryAsync();

                siteDefinitions = definitions.ToArray();
                siteSubscriptions = subscriptions.ToArray();
            }
            catch (ServerException ex)
            {
                // If there is no workflow service present in the farm this method will throw an error. 
                scannerBase.Logger.Error(ex, $"No workflow service present!");
            }

            // We've found SP2013 workflows
            if (siteDefinitions != null && siteDefinitions.Length > 0)
            {
                foreach (var listDefinition in siteDefinitions)
                {
                    // Check if this workflow is also in use
                    var listWorkflowSubscriptions = siteSubscriptions.Where(p => p.DefinitionId.Equals(listDefinition.Id));

                    // Perform workflow analysis
                    WorkflowActionAnalysis workFlowAnalysisResult = null;
                    WorkflowTriggerAnalysis workFlowTriggerAnalysisResult = null;
                    if (workflowOptions.Analyze)
                    {
                        workFlowAnalysisResult = WorkflowManager.Instance.ParseWorkflowDefinition(listDefinition.Xaml);
                        workFlowTriggerAnalysisResult = WorkflowManager.ParseWorkflowTriggers(GetWorkflowPropertyBool(listDefinition.Properties, "SPDConfig.StartOnCreate"),
                                                                                              GetWorkflowPropertyBool(listDefinition.Properties, "SPDConfig.StartOnChange"),
                                                                                              GetWorkflowPropertyBool(listDefinition.Properties, "SPDConfig.StartManually"));
                    }

                    string workflowScope = "";

                    if (listDefinition.RestrictToType != null && listDefinition.RestrictToType.Equals("list", StringComparison.InvariantCultureIgnoreCase))
                    {
                        workflowScope = "List";
                    }
                    else if (listDefinition.RestrictToType != null && listDefinition.RestrictToType.Equals("site", StringComparison.InvariantCultureIgnoreCase))
                    {
                        workflowScope = "Site";
                    }
                    else
                    {
                        workflowScope = "Universal";
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
                                ScanId = scannerBase.ScanId,
                                SiteUrl = scannerBase.SiteUrl,
                                WebUrl = scannerBase.WebUrl,
                                ListTitle = associatedListTitle,
                                ListUrl = associatedListUrl,
                                ListId = associatedListId,
                                ContentTypeId = associatedContentTypeId,
                                ContentTypeName = ScannerBase.Clean(associatedContentTypeName),
                                Scope = workflowScope,
                                RestrictToType = workflowScope,
                                DefinitionName = ScannerBase.Clean(listDefinition.DisplayName),
                                DefinitionDescription = ScannerBase.Clean(listDefinition.Description),
                                SubscriptionName = ScannerBase.Clean(listWorkflowSubscription.Name),
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
                            ScanId = scannerBase.ScanId,
                            SiteUrl = scannerBase.SiteUrl,
                            WebUrl = scannerBase.WebUrl,
                            ListTitle = "",
                            ListUrl = "",
                            ListId = Guid.Empty,
                            ContentTypeId = "",
                            ContentTypeName = "",
                            Scope = workflowScope,
                            RestrictToType = workflowScope,
                            DefinitionName = ScannerBase.Clean(listDefinition.DisplayName),
                            DefinitionDescription = ScannerBase.Clean(listDefinition.Description),
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
                await scannerBase.StorageManager.StoreWorkflowInformationAsync(scannerBase.ScanId, workflowLists);
            }
        }

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

            await clientContext.ExecuteQueryAsync();

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
