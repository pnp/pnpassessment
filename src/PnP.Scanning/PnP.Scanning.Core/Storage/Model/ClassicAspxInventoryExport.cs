#nullable enable
namespace PnP.Scanning.Core.Storage;

/// <summary>Canonical 21-column CSV row for one observed physical ASPX file, with reversible nullable text.</summary>
internal sealed class ClassicAspxInventoryExport
{
    public Guid ScanId { get; set; }
    public string AspxKey { get; set; } = string.Empty;
    public Guid SiteCollectionId { get; set; }
    public Guid? WebId { get; set; }
    public Guid? ListId { get; set; }
    public Guid? FolderUniqueId { get; set; }
    public Guid FileUniqueId { get; set; }
    [CsvHelper.Configuration.Attributes.TypeConverter(typeof(ClassicAspxNullableStringConverter))]
    public string? SiteUrl { get; set; }
    [CsvHelper.Configuration.Attributes.TypeConverter(typeof(ClassicAspxNullableStringConverter))]
    public string? WebUrl { get; set; }
    [CsvHelper.Configuration.Attributes.TypeConverter(typeof(ClassicAspxNullableStringConverter))]
    public string? Url { get; set; }
    [CsvHelper.Configuration.Attributes.TypeConverter(typeof(ClassicAspxNullableStringConverter))]
    public string? FileName { get; set; }
    [CsvHelper.Configuration.Attributes.TypeConverter(typeof(ClassicAspxNullableStringConverter))]
    public string? ContentTypeId { get; set; }
    [CsvHelper.Configuration.Attributes.TypeConverter(typeof(ClassicAspxNullableStringConverter))]
    public string? CustomizedPageStatus { get; set; }
    [CsvHelper.Configuration.Attributes.TypeConverter(typeof(ClassicAspxNullableStringConverter))]
    public string? SetupPath { get; set; }
    public string FileReadState { get; set; } = ClassicAspxReadStates.NotAttempted;
    [CsvHelper.Configuration.Attributes.TypeConverter(typeof(ClassicAspxNullableStringConverter))]
    public string? FileErrorCodes { get; set; }
    [CsvHelper.Configuration.Attributes.TypeConverter(typeof(ClassicAspxNullableStringConverter))]
    public string? FileErrorDetail { get; set; }
    public string ProvenanceJson { get; set; } = "[]";
    public string CoverageState { get; set; } = ClassicAspxCoverageStates.Unknown;
    public DateTime FirstObservedAtUtc { get; set; }
    public DateTime ObservedAtUtc { get; set; }
}
