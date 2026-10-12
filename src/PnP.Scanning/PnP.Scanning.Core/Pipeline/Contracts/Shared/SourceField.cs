#nullable enable
using System.Globalization;
using System.Text.Json;

namespace PnP.Scanning.Core.Pipeline.Contracts.Shared;

/// <summary>
/// Preserves a captured field type and JSON value, including explicit null, without retaining a SharePoint SDK object.
/// </summary>
internal sealed record SourceField(string Type, JsonElement Value)
{
    /// <summary>
    /// Captures a value as detached JSON with the supplied source type label.
    /// </summary>
    internal static SourceField Of(string type, object? value) => new(type, JsonSerializer.SerializeToElement(value));
    internal string? Text => Value.ValueKind == JsonValueKind.Null ? null : Value.ValueKind == JsonValueKind.String ? Value.GetString() : Value.ToString();
    /// <summary>
    /// Restores supported scalar types for offline rules while preserving null and invariant representations.
    /// </summary>
    internal object? ToValue() => Value.ValueKind == JsonValueKind.Null ? null : Type switch
    {
        "DateTime" => DateTime.Parse(Text!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        "Guid" => Guid.Parse(Text!),
        "Int32" => Value.GetInt32(),
        "Boolean" => Value.GetBoolean(),
        _ => Text,
    };
}
