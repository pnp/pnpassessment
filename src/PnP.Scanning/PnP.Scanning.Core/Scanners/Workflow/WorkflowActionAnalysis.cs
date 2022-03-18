namespace PnP.Scanning.Core.Scanners
{
    internal sealed class WorkflowActionAnalysis
    {

        internal WorkflowActionAnalysis()
        {
            WorkflowActions = new List<string>();
            UnsupportedActions = new List<string>();
        }

        internal List<string> WorkflowActions { get; set; }
        
        internal int ActionCount { get; set; }
        
        internal List<string> UnsupportedActions { get; set; }

        internal int UnsupportedAccountCount { get; set; }
    }
}
