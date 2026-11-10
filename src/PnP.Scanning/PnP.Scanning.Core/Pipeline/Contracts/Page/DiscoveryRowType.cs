#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PnP.Scanning.Core.Pipeline.Contracts.Page;

/// <summary>
/// Identifies a discovery record's role independently of its scope, acquisition status, and final page classification.
/// </summary>
[JsonConverter(typeof(DiscoveryRowTypeJsonConverter))]
internal enum DiscoveryRowType
{
    /// <summary>Marks an observed physical page candidate, not an already unified analysis Page.</summary>
    Page = 1,

    /// <summary>Describes a discovery scope or acquisition surface and its coverage.</summary>
    Scope,

    /// <summary>Retains a declared reference and its optional physical-file resolution.</summary>
    Reference,

    /// <summary>Records one native paginated acquisition request and response.</summary>
    Pagination,

    /// <summary>Identifies missing or unresolved acquisition evidence.</summary>
    Gap,

    /// <summary>Aggregates the assessment's discovery coverage without adding an object.</summary>
    Summary,

    /// <summary>Retains a legacy content-assessment outcome independently of object existence.</summary>
    Assessment,
}

/// <summary>
/// Preserves established discovery-kind text in snapshots and legacy mappings, rejecting unset, numeric, or unrecognized kinds.
/// </summary>
internal sealed class DiscoveryRowTypeJsonConverter : JsonConverter<DiscoveryRowType>
{
    public override bool HandleNull => true;

    /// <summary>
    /// Requires an exact known text discriminator without turning absent or unknown evidence into a valid record kind.
    /// </summary>
    public override DiscoveryRowType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Discovery RowType must be an explicit known text value.");
        return reader.GetString() switch
        {
            "Page" => DiscoveryRowType.Page,
            "Scope" => DiscoveryRowType.Scope,
            "Reference" => DiscoveryRowType.Reference,
            "Pagination" => DiscoveryRowType.Pagination,
            "Gap" => DiscoveryRowType.Gap,
            "Summary" => DiscoveryRowType.Summary,
            "Assessment" => DiscoveryRowType.Assessment,
            _ => throw new JsonException("Discovery RowType is not a recognized evidence kind."),
        };
    }

    /// <summary>
    /// Writes only the existing case-sensitive text labels, never an enum number or a successful-looking default.
    /// </summary>
    public override void Write(Utf8JsonWriter writer, DiscoveryRowType value, JsonSerializerOptions options) =>
        writer.WriteStringValue(
            value switch
            {
                DiscoveryRowType.Page => "Page",
                DiscoveryRowType.Scope => "Scope",
                DiscoveryRowType.Reference => "Reference",
                DiscoveryRowType.Pagination => "Pagination",
                DiscoveryRowType.Gap => "Gap",
                DiscoveryRowType.Summary => "Summary",
                DiscoveryRowType.Assessment => "Assessment",
                _ => throw new JsonException("Discovery RowType is unset or is not a recognized evidence kind."),
            });
}