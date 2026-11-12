#nullable enable
using System.Text.Json;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Storage.Pipeline;

namespace PnP.Scanning.Core.Pipeline.Orchestration;

// Runtime configuration uses the existing versioned phase parameters, not additional schema columns.
/// <summary>
/// Persists runtime options and analysis selection inside the existing versioned phase parameters.
/// </summary>
internal sealed record PipelineRunSettings(
    int Threads,
    string CollectionParametersJson,
    string AnalysisParametersJson,
    string? CollectionOptionsJson = null,
    Guid? AnalysisRunId = null)
{
    /// <summary>
    /// Encodes runtime selection in the existing versioned phase-parameter envelope.
    /// </summary>
    internal string Serialize() => VersionedJson.From(new { pipelineRuntimeVersion = 1, runtime = this }).Json;

    /// <summary>
    /// Reads required pipeline runtime settings from the bound persisted phase.
    /// </summary>
    internal static PipelineRunSettings Read(PhaseRunRow run) =>
        new VersionedJson(run.ParametersJson).Value.GetProperty("runtime").Deserialize<PipelineRunSettings>() ??
        throw new InvalidDataException("Pipeline runtime configuration is absent.");

    /// <summary>
    /// Extracts internal analysis parameters while preserving unrelated foundation parameter shapes.
    /// </summary>
    internal static string AnalysisParameters(PhaseRunRow run)
    {
        JsonElement value = new VersionedJson(run.ParametersJson).Value;
        return value.ValueKind == JsonValueKind.Object &&
               value.TryGetProperty("pipelineRuntimeVersion", out JsonElement format) &&
               format.ValueKind == JsonValueKind.Number && format.GetInt32() == 1
            ? Read(run).AnalysisParametersJson
            : run.ParametersJson;
    }
}