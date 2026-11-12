namespace PnP.Scanning.Core.Services;

// Phase selection stays internal. Public callers continue to use Start and the existing lifecycle RPCs.
/// <summary>
/// Selects an internal collection module, fixed options, and versioned parameters.
/// </summary>
internal sealed class CollectRequest
{
    public string Module { get; set; } = "";
    public StartRequest CollectionOptions { get; set; }
    public string ParametersJson { get; set; } = "";
}

/// <summary>
/// Selects an internal sealed snapshot, rule, parameters, and analysis concurrency.
/// </summary>
internal sealed class AnalyzeRequest
{
    public string Id { get; set; } = "";
    public string SnapshotId { get; set; } = "";
    public string RuleVersion { get; set; } = "";
    public string ParametersJson { get; set; } = "";
    public int Threads { get; set; }
}

/// <summary>
/// Combines an internal collection request with pinned offline-analysis selection.
/// </summary>
internal sealed class StartPipelineRequest
{
    public CollectRequest Collection { get; set; }
    public string RuleVersion { get; set; } = "";
    public string AnalysisParametersJson { get; set; } = "";
}

/// <summary>
/// Returns the fixed assessment, snapshot, run, and rule identities acknowledged at enqueue.
/// </summary>
internal sealed class PhaseReply
{
    public string AssessmentId { get; set; } = "";
    public string SnapshotId { get; set; } = "";
    public string RunId { get; set; } = "";
    public string AnalysisRunId { get; set; } = "";
    public string Status { get; set; } = "";
    public string RuleVersion { get; set; } = "";
}