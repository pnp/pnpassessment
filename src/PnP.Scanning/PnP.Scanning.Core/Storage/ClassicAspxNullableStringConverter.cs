#nullable enable
using CsvHelper;
using CsvHelper.Configuration;
using CsvHelper.TypeConversion;

namespace PnP.Scanning.Core.Storage;

/// <summary>Reversible nullable-string wire contract for the two M1 CSVs only.</summary>
internal sealed class ClassicAspxNullableStringConverter : DefaultTypeConverter
{
    internal const string EncodingVersion = "BackslashNullV1";
    internal const string NullToken = @"\N";
    /// <summary>Preserves null, empty, and literal leading-backslash strings as distinct reversible M1 CSV values.</summary>
    internal static string Encode(string? value) => value == null ? NullToken :
        value.StartsWith("\\", StringComparison.Ordinal) ? "\\" + value : value;

    /// <summary>Restores the null token and removes exactly one escape backslash from encoded non-null strings.</summary>
    internal static string? Decode(string? value)
    {
        if (value == null || value == NullToken)
        {
            return null;
        }
        return value.StartsWith("\\\\", StringComparison.Ordinal) ? value[1..] : value;
    }

    /// <summary>Applies the M1 nullable-string encoding to the typed CSV field without altering other converter behavior.</summary>
    public override string ConvertToString(object? value, IWriterRow row, MemberMapData memberMapData) =>
        Encode((string?)value);

    /// <summary>Decodes a CSV field using the same null and leading-backslash contract as the writer.</summary>
    public override object? ConvertFromString(string? text, IReaderRow row, MemberMapData memberMapData) =>
        Decode(text);
}
