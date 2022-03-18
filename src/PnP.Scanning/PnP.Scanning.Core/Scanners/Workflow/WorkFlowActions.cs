namespace PnP.Scanning.Core.Scanners
{

    internal sealed class WorkflowAction
    {
        internal string ActionName { get; set; }

        internal string ActionNameShort { get; set; }
    }

    internal sealed class WorkflowActions
    {
        internal WorkflowActions()
        {
            SP2010DefaultActions = new List<WorkflowAction>();
            SP2013DefaultActions = new List<WorkflowAction>();
        }

        internal List<WorkflowAction> SP2010DefaultActions { get; set; }

        internal List<WorkflowAction> SP2013DefaultActions { get; set; }

    }
}
