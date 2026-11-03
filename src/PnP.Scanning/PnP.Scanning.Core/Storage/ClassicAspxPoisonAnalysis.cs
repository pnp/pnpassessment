#nullable enable
using System.Text.Json;

namespace PnP.Scanning.Core.Storage;

/// <summary>Interprets the server's original NoExecute field as one-bit evidence, never as a complete DocFlags mask.</summary>
internal static class ClassicAspxPoisonAnalysis
{
    internal const string Source = "SPO.ListItem.NoExecute";
    internal const string EvidenceKind = "ServerNoExecuteField";
    internal const string Unknown = "Unknown";
    internal const string Poisoned = "Poisoned";
    internal const string NotPoisoned = "NotPoisoned";
    internal const string NoExecuteDownload = "NoExecuteDownload";
    internal const string NotBlockedByNoExecute = "NotBlockedByNoExecute";
    private const string MetadataChangedObservation = "metadata_changed";
    private const string BlockedValue = "1";
    private const string NotBlockedValue = "0";

    /// <summary>Requires an available server NoExecute observation and fails closed if the writer retained conflicting one-bit evidence.</summary>
    internal static Analysis Analyze(SPFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return AnalyzeRaw(file.NoExecuteRaw, file.NoExecuteReadState, file.NoExecuteSource,
            hasConflict: HasNoExecuteConflict(file.ProvenanceJson));
    }

    /// <summary>Analyzes only original server zero/one values from the declared field source, leaving unavailable, invalid or conflicting input unknown without altering its raw text.</summary>
    internal static Analysis AnalyzeRaw(string? raw, string readState, string? source, bool hasConflict = false)
    {
        if (source != Source || readState != ClassicAspxReadStates.Available || hasConflict ||
            raw is not (BlockedValue or NotBlockedValue))
        {
            return new Analysis(null, Unknown, Unknown);
        }
        var poisoned = raw == BlockedValue;
        return new Analysis(poisoned, poisoned ? Poisoned : NotPoisoned,
            poisoned ? NoExecuteDownload : NotBlockedByNoExecute);
    }

    /// <summary>Rejects conflicting NoExecute raw/source/read-state evidence or malformed provenance rather than trusting a retained first observation.</summary>
    private static bool HasNoExecuteConflict(string provenance)
    {
        try
        {
            using var document = JsonDocument.Parse(provenance);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return true;
            }
            foreach (var evidence in document.RootElement.EnumerateArray())
            {
                if (evidence.ValueKind == JsonValueKind.Object &&
                    evidence.TryGetProperty("observation", out var observation) &&
                    observation.ValueKind == JsonValueKind.String && observation.GetString() == MetadataChangedObservation &&
                    evidence.TryGetProperty("field", out var field) && field.ValueKind == JsonValueKind.String &&
                    field.GetString() is nameof(SPFile.NoExecuteRaw) or nameof(SPFile.NoExecuteSource) or nameof(SPFile.NoExecuteReadState))
                {
                    return true;
                }
            }
            return false;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    /// <summary>Reports the server NoExecute-field disposition only, not a guarantee of actual HTTP rendering behavior or a complete DocFlags value.</summary>
    internal sealed record Analysis(bool? IsPoisoned, string PoisonState, string RenderDisposition);
}
