#nullable enable
using System.Globalization;
using System.Text.Json;

namespace PnP.Scanning.Core.ClassicPages.Contracts.Shared;

/// <summary>
/// Preserves a captured field's declared kind and detached JSON value, including explicit null.
/// </summary>
internal sealed record SourceField(string Type, JsonElement Value)
{
    /// <summary>
    /// Captures a typed value as detached JSON, including explicit null.
    /// </summary>
    internal static SourceField Of(string type, object? value) => new(type, JsonSerializer.SerializeToElement(value));

    internal string? Text =>
        Value.ValueKind == JsonValueKind.Null
            ? null
            : Value.ValueKind == JsonValueKind.String
                ? Value.GetString()
                : Value.ToString();

    /// <summary>
    /// Restores the declared scalar or structured value and rejects unsupported captured types.
    /// </summary>
    internal object? ToValue() =>
        Value.ValueKind == JsonValueKind.Null
            ? null
            : Type switch
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