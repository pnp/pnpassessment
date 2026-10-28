#nullable enable
using CsvHelper;
using CsvHelper.Configuration;
using CsvHelper.TypeConversion;

namespace PnP.Scanning.Core.Storage;

/// <summary>Reversible nullable-string wire contract for the two M1 CSVs only.</summary>
internal sealed class ClassicAspxNullableStringConverter : DefaultTypeConverter
{
    internal const string NullToken = @"\N";
    internal static string Encode(string? value) => value == null ? NullToken :
        value.StartsWith("\\", StringComparison.Ordinal) ? "\\" + value : value;

    internal static string? Decode(string? value)
    {
        if (value == null || value == NullToken) return null;
        return value.StartsWith("\\\\", StringComparison.Ordinal) ? value[1..] : value;
    }

    public override string ConvertToString(object? value, IWriterRow row, MemberMapData memberMapData) =>
        Encode((string?)value);

    public override object? ConvertFromString(string? text, IReaderRow row, MemberMapData memberMapData) =>
        Decode(text);
}
