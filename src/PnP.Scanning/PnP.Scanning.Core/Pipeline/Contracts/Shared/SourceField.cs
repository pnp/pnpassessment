#nullable enable
using System.Globalization;
using System.Text.Json;

namespace PnP.Scanning.Core.Pipeline.Contracts.Shared;

internal sealed record SourceField(string Type, JsonElement Value)
{
    internal static SourceField Of(string type, object? value) => new(type, JsonSerializer.SerializeToElement(value));
    internal string? Text => Value.ValueKind == JsonValueKind.Null ? null : Value.ValueKind == JsonValueKind.String ? Value.GetString() : Value.ToString();
    internal object? ToValue() => Value.ValueKind == JsonValueKind.Null ? null : Type switch
    {
        "DateTime" => DateTime.Parse(Text!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        "Guid" => Guid.Parse(Text!),
        "Int32" => Value.GetInt32(),
        "Int64" => Value.GetInt64(),
        "Double" => Value.GetDouble(),
        "Decimal" => Value.GetDecimal(),
        "Boolean" => Value.GetBoolean(),
        "String" => Value.GetString(),
        "User" or "Url" or "Lookup" or "Json" => Value.Clone(),
        _ => throw new InvalidDataException($"Unsupported captured field type '{Type}'."),
    };
}
