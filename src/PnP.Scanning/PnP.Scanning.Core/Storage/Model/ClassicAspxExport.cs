#nullable enable
using CsvHelper.Configuration.Attributes;

namespace PnP.Scanning.Core.Storage;

/// <summary>Fixed 24-column business export of one selected physical classic or unclassified ASPX file.</summary>
internal sealed class ClassicAspxExport
{
    [Index(0)]
    public Guid ScanId { get; set; }

    [Index(1)]
    public Guid SiteCollectionId { get; set; }

    [Index(2)]
    public Guid? WebId { get; set; }

    [Index(3)]
    public Guid? ListId { get; set; }

    [Index(4)]
    public Guid FileUniqueId { get; set; }

    [Index(5), TypeConverter(typeof(ClassicAspxNullableStringConverter))]
    public string? SiteUrl { get; set; }

    [Index(6), TypeConverter(typeof(ClassicAspxNullableStringConverter))]
    public string? WebUrl { get; set; }

    [Index(7), TypeConverter(typeof(ClassicAspxNullableStringConverter))]
    public string? Url { get; set; }

    [Index(8), TypeConverter(typeof(ClassicAspxNullableStringConverter))]
    public string? FileName { get; set; }

    [Index(9), TypeConverter(typeof(ClassicAspxNullableStringConverter))]
    public string? ClassicPageType { get; set; }

    [Index(10)]
    public string PageRole { get; set; } = string.Empty;

    [Index(11)]
    public string ClassificationState { get; set; } = string.Empty;

    [Index(12)]
    public string ClassificationReason { get; set; } = string.Empty;

    [Index(13), TypeConverter(typeof(ClassicAspxNullableStringConverter))]
    public string? ContentTypeId { get; set; }

    [Index(14), TypeConverter(typeof(ClassicAspxNullableStringConverter))]
    public string? CustomizedPageStatus { get; set; }

    [Index(15), TypeConverter(typeof(ClassicAspxNullableStringConverter))]
    public string? SetupPath { get; set; }

    [Index(16), TypeConverter(typeof(ClassicAspxNullableStringConverter))]
    public string? NoExecuteRaw { get; set; }

    [Index(17)]
    public bool? IsPoisoned { get; set; }

    [Index(18)]
    public string PoisonState { get; set; } = string.Empty;

    [Index(19)]
    public string RenderDisposition { get; set; } = string.Empty;

    [Index(20)]
    public string MetadataReadState { get; set; } = string.Empty;

    [Index(21), TypeConverter(typeof(ClassicAspxNullableStringConverter))]
    public string? ErrorCodes { get; set; }

    [Index(22), TypeConverter(typeof(ClassicAspxNullableStringConverter))]
    public string? ErrorDetail { get; set; }

    [Index(23)]
    public DateTime ObservedAtUtc { get; set; }
}
