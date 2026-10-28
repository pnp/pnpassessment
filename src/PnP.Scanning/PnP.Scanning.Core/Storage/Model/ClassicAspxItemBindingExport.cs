#nullable enable
namespace PnP.Scanning.Core.Storage;

/// <summary>Typed normalized binding CSV row with reversible nullable-string metadata, not an EF entity.</summary>
internal sealed class ClassicAspxItemBindingExport
{
    public Guid ScanId { get; set; }
    public string AspxKey { get; set; } = string.Empty;
    public Guid SiteCollectionId { get; set; }
    public Guid WebId { get; set; }
    public Guid ListId { get; set; }
    public int ItemId { get; set; }
    public Guid? ItemGuid { get; set; }
    public string ItemGuidReadState { get; set; } = "NotAttempted";
    [CsvHelper.Configuration.Attributes.TypeConverter(typeof(ClassicAspxNullableStringConverter))]
    public string? SiteUrl { get; set; }
    [CsvHelper.Configuration.Attributes.TypeConverter(typeof(ClassicAspxNullableStringConverter))]
    public string? WebUrl { get; set; }
    [CsvHelper.Configuration.Attributes.TypeConverter(typeof(ClassicAspxNullableStringConverter))]
    public string? ItemUrl { get; set; }
    [CsvHelper.Configuration.Attributes.TypeConverter(typeof(ClassicAspxNullableStringConverter))]
    public string? ItemTitle { get; set; }
    [CsvHelper.Configuration.Attributes.TypeConverter(typeof(ClassicAspxNullableStringConverter))]
    public string? ContentTypeId { get; set; }
    public ClassicAspxBindingKinds BindingKinds { get; set; }
    public string BindingProvenanceJson { get; set; } = "[]";
    public DateTime ObservedAtUtc { get; set; }
}
