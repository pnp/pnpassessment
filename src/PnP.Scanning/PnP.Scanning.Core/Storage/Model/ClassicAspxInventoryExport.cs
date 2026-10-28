#nullable enable
namespace PnP.Scanning.Core.Storage;

/// <summary>Typed native CSV row. Zero bindings are observed in this declared scan scope, not tenant completeness.</summary>
internal sealed class ClassicAspxInventoryExport
{
    public Guid ScanId { get; set; }
    public string AspxKey { get; set; } = string.Empty;
    public string IdentityKind { get; set; } = ClassicAspxIdentityKinds.TenantFile;
    public int IdentityVersion { get; set; } = 1;
    public Guid? SiteCollectionId { get; set; }
    public Guid? FileUniqueId { get; set; }
    public Guid? WebId { get; set; }
    [CsvHelper.Configuration.Attributes.TypeConverter(typeof(ClassicAspxNullableStringConverter))]
    public string? SiteUrl { get; set; }
    [CsvHelper.Configuration.Attributes.TypeConverter(typeof(ClassicAspxNullableStringConverter))]
    public string? WebUrl { get; set; }
    [CsvHelper.Configuration.Attributes.TypeConverter(typeof(ClassicAspxNullableStringConverter))]
    public string? Url { get; set; }
    [CsvHelper.Configuration.Attributes.TypeConverter(typeof(ClassicAspxNullableStringConverter))]
    public string? FileName { get; set; }
    [CsvHelper.Configuration.Attributes.TypeConverter(typeof(ClassicAspxNullableStringConverter))]
    public string? PageType { get; set; }
    [CsvHelper.Configuration.Attributes.TypeConverter(typeof(ClassicAspxNullableStringConverter))]
    public string? ContentTypeId { get; set; }
    [CsvHelper.Configuration.Attributes.TypeConverter(typeof(ClassicAspxNullableStringConverter))]
    public string? CustomizedPageStatus { get; set; }
    [CsvHelper.Configuration.Attributes.TypeConverter(typeof(ClassicAspxNullableStringConverter))]
    public string? SetupPath { get; set; }
    public string FileReadState { get; set; } = "NotAttempted";
    [CsvHelper.Configuration.Attributes.TypeConverter(typeof(ClassicAspxNullableStringConverter))]
    public string? FileErrorCodes { get; set; }
    [CsvHelper.Configuration.Attributes.TypeConverter(typeof(ClassicAspxNullableStringConverter))]
    public string? FileErrorDetail { get; set; }
    [CsvHelper.Configuration.Attributes.TypeConverter(typeof(ClassicAspxNullableStringConverter))]
    public string? HandlerDeclaredType { get; set; }
    [CsvHelper.Configuration.Attributes.TypeConverter(typeof(ClassicAspxNullableStringConverter))]
    public string? HandlerNormalizedType { get; set; }
    [CsvHelper.Configuration.Attributes.TypeConverter(typeof(ClassicAspxNullableStringConverter))]
    public string? HandlerEffectiveType { get; set; }
    public string HandlerReadState { get; set; } = "NotAttempted";
    public string HandlerParseState { get; set; } = "NotAttempted";
    [CsvHelper.Configuration.Attributes.TypeConverter(typeof(ClassicAspxNullableStringConverter))]
    public string? HandlerSource { get; set; }
    [CsvHelper.Configuration.Attributes.TypeConverter(typeof(ClassicAspxNullableStringConverter))]
    public string? HandlerDefaultProvenance { get; set; }
    [CsvHelper.Configuration.Attributes.TypeConverter(typeof(ClassicAspxNullableStringConverter))]
    public string? HandlerReasonCode { get; set; }
    [CsvHelper.Configuration.Attributes.TypeConverter(typeof(ClassicAspxNullableStringConverter))]
    public string? HandlerErrorCodes { get; set; }
    [CsvHelper.Configuration.Attributes.TypeConverter(typeof(ClassicAspxNullableStringConverter))]
    public string? HandlerErrorDetail { get; set; }
    [CsvHelper.Configuration.Attributes.TypeConverter(typeof(ClassicAspxNullableStringConverter))]
    public string? CatalogSource { get; set; }
    [CsvHelper.Configuration.Attributes.TypeConverter(typeof(ClassicAspxNullableStringConverter))]
    public string? CatalogVersion { get; set; }
    [CsvHelper.Configuration.Attributes.TypeConverter(typeof(ClassicAspxNullableStringConverter))]
    public string? CatalogDigest { get; set; }
    [CsvHelper.Configuration.Attributes.TypeConverter(typeof(ClassicAspxNullableStringConverter))]
    public string? CatalogEntryKey { get; set; }
    public string ProvenanceJson { get; set; } = "[]";
    public string CoverageState { get; set; } = "Unknown";
    public DateTime FirstObservedAtUtc { get; set; }
    public DateTime ObservedAtUtc { get; set; }
    public long DistinctItemBindingCount { get; set; }
    public string BindingCountBasis { get; set; } = "DeclaredScanScopeObserved";
}
