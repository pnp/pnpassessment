#nullable enable
using System.Text.Json;

namespace PnP.Scanning.Core.Pipeline.Contracts.Shared;

/// <summary>
/// Validates and preserves a module payload together with its positive schema version, independently of database schema versions.
/// </summary>
internal sealed record VersionedJson
{
    public static VersionedJson Empty { get; } = new("{\"schemaVersion\":1,\"value\":{}}");

    /// <summary>
    /// Parses an envelope, requires a positive schemaVersion and a value, and retains detached JSON for persistence.
    /// </summary>
    public VersionedJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("schemaVersion", out var version) ||
            !version.TryGetInt32(out var number) || number < 1 ||
            !root.TryGetProperty("value", out _))
            throw new ArgumentException("Module JSON requires a positive schemaVersion and a value.", nameof(json));
        Json = root.GetRawText();
        SchemaVersion = number;
    }

    public string Json { get; }
    public int SchemaVersion { get; }

    public JsonElement Value
    {
        get
        {
            using var document = JsonDocument.Parse(Json);
            return document.RootElement.GetProperty("value").Clone();
        }
    }

    /// <summary>
    /// Serializes a value inside the versioned envelope used for sources, checkpoints, analysis parameters and results.
    /// </summary>
    public static VersionedJson From<T>(T value, int schemaVersion = 1) =>
        new(JsonSerializer.Serialize(new { schemaVersion, value }));
}
