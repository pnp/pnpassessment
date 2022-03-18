namespace PnP.Scanning.Core.Scanners
{
    internal sealed class WorkflowTriggerAnalysis
    {
        internal WorkflowTriggerAnalysis()
        {
            WorkflowTriggers = new List<string>();
            UnSupportedTriggers = new List<string>();
        }

        internal List<string> WorkflowTriggers { get; set; }

        internal List<string> UnSupportedTriggers { get; set; }
    }
}
