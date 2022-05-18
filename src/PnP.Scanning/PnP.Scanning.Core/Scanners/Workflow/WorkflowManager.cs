using Serilog;
using System.Xml;
using System.Xml.Serialization;

namespace PnP.Scanning.Core.Scanners
{
    /// <summary>
    /// Class to handle workflow analysis
    /// </summary>
    internal sealed class WorkflowManager
    {
        private static readonly Lazy<WorkflowManager> lazyInstance = new(() => new WorkflowManager());
        private WorkflowActions defaultWorkflowActions;


        private static readonly string[] SP2013ExcludedFromCalculationActions = new string[]
        {
            "Microsoft.SharePoint.WorkflowServices.Activities.Comment",
            "Microsoft.SharePoint.WorkflowServices.Activities.WriteToHistory",
            "Microsoft.SharePoint.WorkflowServices.Activities.SetWorkflowStatus"
        };

        private static readonly string[] SP2013SupportedFlowActions = new string[]
        {
            "Microsoft.SharePoint.WorkflowServices.Activities.SetWorkflowStatus",
            "Microsoft.SharePoint.WorkflowServices.Activities.Comment",
            "Microsoft.SharePoint.WorkflowServices.Activities.CallHTTPWebService",
            "Microsoft.Activities.BuildDynamicValue",
            "Microsoft.Activities.GetDynamicValueProperty",
            "Microsoft.Activities.CountDynamicValueItems",
            "Microsoft.SharePoint.WorkflowServices.Activities.SetField",
            "System.Activities.Statements.Assign",
            "Microsoft.SharePoint.WorkflowServices.Activities.CreateListItem",
            "Microsoft.SharePoint.WorkflowServices.Activities.UpdateListItem",
            "Microsoft.SharePoint.WorkflowServices.Activities.DeleteListItem",
            "Microsoft.SharePoint.WorkflowServices.Activities.CheckOutItem",
            "Microsoft.SharePoint.WorkflowServices.Activities.UndoCheckOutItem",
            "Microsoft.SharePoint.WorkflowServices.Activities.CheckInItem",
            "Microsoft.SharePoint.WorkflowServices.Activities.CopyItem",
            "Microsoft.SharePoint.WorkflowServices.Activities.Email",
            "Microsoft.Activities.Expressions.AddToDate",
            "Microsoft.SharePoint.WorkflowServices.Activities.SetTimeField",
            "Microsoft.SharePoint.WorkflowServices.Activities.DateInterval",
            "Microsoft.SharePoint.WorkflowServices.Activities.ExtractSubstringFromEnd",
            "Microsoft.SharePoint.WorkflowServices.Activities.ExtractSubstringFromStart",
            "Microsoft.SharePoint.WorkflowServices.Activities.ExtractSubstringFromIndex",
            "Microsoft.SharePoint.WorkflowServices.Activities.ExtractSubstringFromIndexLength",
            "Microsoft.Activities.Expressions.Trim",
            "Microsoft.Activities.Expressions.IndexOfString",
            "Microsoft.Activities.Expressions.ReplaceString",
            "Microsoft.SharePoint.WorkflowServices.Activities.DelayFor",
            "Microsoft.SharePoint.WorkflowServices.Activities.DelayUntil",
            "Microsoft.SharePoint.WorkflowServices.Activities.Calc",
            "Microsoft.SharePoint.WorkflowServices.Activities.WriteToHistory",
            "Microsoft.SharePoint.WorkflowServices.Activities.TranslateDocument",
            "Microsoft.SharePoint.WorkflowServices.Activities.SetModerationStatus",
            "Microsoft.SharePoint.WorkflowServices.Activities.WorkflowInterop",
        };

        // Added here for reference, not used in code
        private static readonly string[] SP2013UnsupportedFlowActions = new string[]
        {
            "Microsoft.SharePoint.WorkflowServices.Activities.WaitForFieldChange",
            "Microsoft.SharePoint.WorkflowServices.Activities.WaitForItemEvent",
            "Microsoft.SharePoint.WorkflowServices.Activities.SingleTask",
            "Microsoft.SharePoint.WorkflowServices.Activities.CompositeTask",
        };


        /// <summary>
        /// Get's the single workflow manager instance, singleton pattern
        /// </summary>
        internal static WorkflowManager Instance
        {
            get
            {
                return lazyInstance.Value;
            }
        }

        internal List<WorkflowAction> DefaultActions
        {
            get
            {
                return defaultWorkflowActions.SP2013DefaultActions;
            }
        }

        #region Construction
        private WorkflowManager()
        {
            // place for instance initialization code
            defaultWorkflowActions = null;
        }
        #endregion

        /// <summary>
        /// Translate workflow trigger to a string
        /// </summary>
        /// <param name="onItemCreate">On create was set</param>
        /// <param name="onItemChange">on change wat set</param>
        /// <param name="allowManual">manual execution is allowed</param>
        /// <returns>string representation of the used workflow triggers</returns>
        internal static WorkflowTriggerAnalysis ParseWorkflowTriggers(bool onItemCreate, bool onItemChange, bool allowManual)
        {
            List<string> triggers = new();

            if (onItemCreate)
            {
                triggers.Add("OnCreate");
            }

            if (onItemChange)
            {
                triggers.Add("OnChange");
            }

            if (allowManual)
            {
                triggers.Add("Manual");
            }

            return new WorkflowTriggerAnalysis() { WorkflowTriggers = triggers };
        }

        /// <summary>
        /// Analysis a workflow definition and returns the used OOB actions
        /// </summary>
        /// <param name="workflowDefinition">Workflow definition to analyze</param>
        /// <returns>List of OOB actions used in the workflow</returns>
        internal WorkflowActionAnalysis ParseWorkflowDefinition(string workflowDefinition)
        {
            try
            {
                if (string.IsNullOrEmpty(workflowDefinition))
                {
                    return null;
                }

                var xmlDoc = new XmlDocument();
                xmlDoc.Load(GenerateStreamFromString(workflowDefinition));

                string namespaceName = "local";

                var namespacePrefix = string.Empty;
                var namespacePrefix1 = string.Empty;
                XmlNamespaceManager nameSpaceManager = null;
                if (xmlDoc.FirstChild.Attributes != null)
                {
                    var xmlns = xmlDoc.FirstChild.Attributes[$"xmlns:{namespaceName}"];
                    if (xmlns != null)
                    {
                        nameSpaceManager = new XmlNamespaceManager(xmlDoc.NameTable);
                        nameSpaceManager.AddNamespace(namespaceName, xmlns.Value);
                        namespacePrefix = namespaceName + ":";
                    }
                }

                // Grab all nodes with the workflow action namespace (ns0/local)
                var nodes = xmlDoc.SelectNodes($"//{namespacePrefix}*", nameSpaceManager);

                // Iterate over the nodes and "identify the OOB activities"
                List<string> usedOOBWorkflowActivities = new();
                List<string> unsupportedOOBWorkflowActivities = new();
                int actionCounter = 0;
                int knownActionCounter = 0;
                int unsupportedActionCounter = 0;

                foreach (XmlNode node in nodes)
                {
                    ParseXmlNode(usedOOBWorkflowActivities, unsupportedOOBWorkflowActivities, ref actionCounter, ref knownActionCounter, ref unsupportedActionCounter, node);
                }

                return new WorkflowActionAnalysis()
                {
                    WorkflowActions = usedOOBWorkflowActivities,
                    ActionCount = knownActionCounter,
                    UnsupportedActions = unsupportedOOBWorkflowActivities,
                    UnsupportedAccountCount = unsupportedActionCounter
                };
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Error happened while parsing the workflow definition");
            }

            return null;
        }

        private void ParseXmlNode(List<string> usedOOBWorkflowActivities, List<string> unsupportedOOBWorkflowActivities, ref int actionCounter, ref int knownActionCounter, ref int unsupportedActionCounter, XmlNode node)
        {
            actionCounter++;

            WorkflowAction defaultOOBWorkflowAction = defaultWorkflowActions.SP2013DefaultActions.FirstOrDefault(p => p.ActionNameShort == node.LocalName);

            if (defaultOOBWorkflowAction != null)
            {
                knownActionCounter++;
                if (!usedOOBWorkflowActivities.Contains(defaultOOBWorkflowAction.ActionNameShort))
                {
                    usedOOBWorkflowActivities.Add(defaultOOBWorkflowAction.ActionNameShort);
                }

                if (!SP2013SupportedFlowActions.Contains(defaultOOBWorkflowAction.ActionName))
                {
                    // Skip "workflow 2013 specific" activities
                    if (!SP2013ExcludedFromCalculationActions.Contains(defaultOOBWorkflowAction.ActionName))
                    {
                        unsupportedActionCounter++;
                        unsupportedOOBWorkflowActivities.Add(defaultOOBWorkflowAction.ActionNameShort);
                    }
                }
            }
        }

        internal void LoadWorkflowDefaultActions()
        {
            WorkflowActions wfActions = new();

            var sp2013Actions = LoadDefaultActions();

            foreach (var action in sp2013Actions)
            {
                wfActions.SP2013DefaultActions.Add(new WorkflowAction() { ActionName = action, ActionNameShort = GetShortName(action) });
            }

            defaultWorkflowActions = wfActions;
        }

        private static string GetShortName(string action)
        {
            if (action.Contains('.'))
            {
                return action.Substring(action.LastIndexOf(".") + 1);
            }

            return action;
        }

        private static List<string> LoadDefaultActions()
        {
            List<string> wfActionsList = new();

            var wfModelString = "";
            using (Stream stream = typeof(WorkflowManager).Assembly.GetManifestResourceStream("PnP.Scanning.Core.Scanners.Workflow.sp2013wfmodel.xml"))
            {
                using (StreamReader reader = new(stream))
                {
                    wfModelString = reader.ReadToEnd();
                }
            }

            if (!string.IsNullOrEmpty(wfModelString))
            {
                WorkflowInfo wfInformation;
                using (var stream = GenerateStreamFromString(wfModelString))
                {
                    XmlSerializer xmlWorkflowInformation = new(typeof(WorkflowInfo));
                    wfInformation = (WorkflowInfo)xmlWorkflowInformation.Deserialize(stream);
                }

                foreach (var wfAction in wfInformation.Actions.Action)
                {
                    if (!wfActionsList.Contains(wfAction.ClassName))
                    {
                        wfActionsList.Add(wfAction.ClassName);
                    }
                }

            }

            return wfActionsList;
        }

        private static Stream GenerateStreamFromString(string s)
        {
            var stream = new MemoryStream();
            var writer = new StreamWriter(stream);
            writer.Write(s);
            writer.Flush();
            stream.Position = 0;
            return stream;
        }
    }
}
