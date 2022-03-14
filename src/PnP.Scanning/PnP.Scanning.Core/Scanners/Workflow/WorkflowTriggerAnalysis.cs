namespace PnP.Scanning.Core.Scanners
{
    public class WorkflowTriggerAnalysis
    {
        public WorkflowTriggerAnalysis()
        {
            this.WorkflowTriggers = new List<string>();
            this.UnSupportedTriggers = new List<string>();
        }

        public List<string> WorkflowTriggers { get; set; }
        public List<string> UnSupportedTriggers { get; set; }
    }
}
