#nullable enable
using System.Text.Json;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Storage.Pipeline;

namespace PnP.Scanning.Core.Pipeline.Orchestration;

// Runtime configuration uses the existing versioned phase parameters, not additional schema columns.
internal sealed record PipelineRunSettings(
    int Threads, string CollectionParametersJson, string AnalysisParametersJson,
    string? CollectionOptionsJson = null, Guid? AnalysisRunId = null)
{
    internal string Serialize() => VersionedJson.From(new { pipelineRuntimeVersion = 1, runtime = this }).Json;

    internal static PipelineRunSettings Read(PhaseRunRow run) =>
        new VersionedJson(run.ParametersJson).Value.GetProperty("runtime").Deserialize<PipelineRunSettings>()
        ?? throw new InvalidDataException("Pipeline runtime configuration is absent.");

    internal static string AnalysisParameters(PhaseRunRow run)
    {
        var value = new VersionedJson(run.ParametersJson).Value;
        return value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty("pipelineRuntimeVersion", out var format) && format.ValueKind == JsonValueKind.Number &&
            format.GetInt32() == 1
            ? Read(run).AnalysisParametersJson : run.ParametersJson;
    }
}
