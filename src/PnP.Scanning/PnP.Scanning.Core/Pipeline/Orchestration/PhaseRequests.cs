namespace PnP.Scanning.Core.Services;

// Phase selection stays internal. Public callers continue to use Start and the existing lifecycle RPCs.
internal sealed class CollectRequest
{
    public string Module { get; set; } = "";
    public StartRequest CollectionOptions { get; set; }
    public string ParametersJson { get; set; } = "";
}

internal sealed class AnalyzeRequest
{
    public string Id { get; set; } = "";
    public string SnapshotId { get; set; } = "";
    public string RuleVersion { get; set; } = "";
    public string ParametersJson { get; set; } = "";
    public int Threads { get; set; }
}

internal sealed class StartPipelineRequest
{
    public CollectRequest Collection { get; set; }
    public string RuleVersion { get; set; } = "";
    public string AnalysisParametersJson { get; set; } = "";
}

internal sealed class PhaseReply
{
    public string AssessmentId { get; set; } = "";
    public string SnapshotId { get; set; } = "";
    public string RunId { get; set; } = "";
    public string AnalysisRunId { get; set; } = "";
    public string Status { get; set; } = "";
    public string RuleVersion { get; set; } = "";
}
