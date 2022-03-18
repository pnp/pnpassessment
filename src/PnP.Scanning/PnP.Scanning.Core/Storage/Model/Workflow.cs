using Microsoft.EntityFrameworkCore;

namespace PnP.Scanning.Core.Storage
{
    [Index(new string[] { nameof(ScanId), nameof(SiteUrl), nameof(WebUrl), nameof(DefinitionId), nameof(SubscriptionId) }, IsUnique = true)]
    internal class Workflow : BaseScanResult
    {
        public Guid DefinitionId { get; set; }

        public Guid SubscriptionId { get; set; }

        public string ListUrl { get; set; }

        public string ListTitle { get; set; }

        public Guid ListId { get; set; }

        public string ContentTypeId { get; set; }

        public string ContentTypeName { get; set; }

        public bool IsOOBWorkflow { get; set; }

        public string Scope { get; set; }

        public string RestrictToType { get; set; }

        public bool Enabled { get; set; }

        public bool ConsiderUpgradingToFlow
        {
            get
            {
                if ((Scope == "List" || Scope == "Site") &&
                    Enabled && HasSubscriptions)
                {
                    return true;
                }
                else
                {
                    return false;
                }
            }
        }

        public string DefinitionName { get; set; }

        public string DefinitionDescription { get; set; }

        public string SubscriptionName { get; set; }

        public bool HasSubscriptions { get; set; }

        public int ActionCount { get; set; }

        public string UsedActions { get; set; }

        public int ToFLowMappingPercentage
        {
            get
            {
                if (ActionCount == 0)
                {
                    return -1;
                }
                else
                {
                    return (int)((ActionCount - UnsupportedActionCount) / (double)ActionCount * 100);
                }
            }
        }

        public int UnsupportedActionCount { get; set; }

        public string UnsupportedActionsInFlow { get; set; }

        public string UsedTriggers { get; set; }

        public DateTime LastSubscriptionEdit { get; set; }

        public DateTime LastDefinitionEdit { get; set; }

        public int TotalInstances { get; set; }

        public int StartedInstances { get; set; }

        public int NotStartedInstances { get; set; }

        public int CancelledInstances { get; set; }

        public int CancellingInstances { get; set; }

        public int SuspendedInstances { get; set; }

        public int TerminatedInstances { get; set; }

        public int CompletedInstances { get; set; }
    }
}
