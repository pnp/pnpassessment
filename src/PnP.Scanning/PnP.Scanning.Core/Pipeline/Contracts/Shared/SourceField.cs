#nullable enable
using System.Globalization;
using System.Text.Json;

namespace PnP.Scanning.Core.Pipeline.Contracts.Shared;

/// <summary>Preserves field type, explicit null and absence without retaining an SDK value.</summary>
internal sealed record SourceField(string Type, JsonElement Value)
{
    internal static SourceField Of(string type, object? value) => new(type, JsonSerializer.SerializeToElement(value));
    internal string? Text => Value.ValueKind == JsonValueKind.Null ? null : Value.ValueKind == JsonValueKind.String ? Value.GetString() : Value.ToString();
    internal object? ToValue() => Value.ValueKind == JsonValueKind.Null ? null : Type switch
    {
        "DateTime" => DateTime.Parse(Text!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        "Guid" => Guid.Parse(Text!),
        "Int32" => Value.GetInt32(),
        "Boolean" => Value.GetBoolean(),
        _ => Text,
    };
}
