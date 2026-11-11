#nullable enable
using System.Text.Json;
using CsvHelper.Configuration.Attributes;
using PnP.Scanning.Core.ClassicPages.Analysis.Page;
using PnP.Scanning.Core.ClassicPages.Contracts.Page;
using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.ClassicPages.Compatibility;

/// <summary>
/// Exposes every stored physical ASPX fact without business admission filters; parsed source fields retain the captured evidence.
/// </summary>
internal sealed class AspxCsvRow
{
    [Ignore] internal SPFile File { get; }
    [Ignore] internal AspxPageEvidence? Evidence { get; }
    [Ignore] internal AspxSourceFacts? Source { get; }

    /// <summary>True for a registered list view or list form, the List product family; unknown role evidence is false.</summary>
    [Ignore] internal bool IsListFamily => AspxPageClassifier.IsListFamily(IsListView, IsListForm);

    /// <summary>
    /// Reads only the supplied SQL row and its stored original bytes, preserving unknown or conflicting NoExecute as null.
    /// </summary>
    internal AspxCsvRow(SPFile file)
    {
        File = file;
        Evidence = file.AspxEvidenceJson == null
            ? null
            : JsonSerializer.Deserialize<AspxPageEvidence>(file.AspxEvidenceJson) ??
              throw new InvalidDataException("Stored ASPX evidence must be an object.");
        Source = Evidence?.OriginalBytes == null ? null : AspxSourceParser.Parse(Evidence.OriginalBytes);
        NoExecute = ClassicAspxPoisonAnalysis.Analyze(file).IsPoisoned;
    }

    public Guid ScanId => File.ScanId;
    public Guid SiteCollectionId => File.SiteCollectionId;
    public Guid FileUniqueId => File.FileUniqueId;
    public Guid? WebId => File.WebId;
    public Guid? ListId => File.ListId;
    public Guid? FolderUniqueId => File.FolderUniqueId;
    public int? ListItemId => File.ListItemId;
    public long? Length => File.Length;
    public string? SiteUrl => File.SiteUrl;
    public string? WebUrl => File.WebUrl;
    public string? ServerRelativeUrl => File.ServerRelativeUrl;
    public string? FileName => File.FileName;
    public string? ContentTypeId => File.ContentTypeId;
    public string? CustomizedPageStatus => File.CustomizedPageStatus;
    public string? SetupPath => File.SetupPath;
    public string? FileReadState => File.FileReadState;
    public string? FileErrorCodes => File.FileErrorCodes;
    public string? FileErrorDetail => File.FileErrorDetail;
    public string ProvenanceJson => File.ProvenanceJson;
    public string? CoverageState => File.CoverageState;
    public DateTime FirstObservedAtUtc => File.FirstObservedAtUtc;
    public DateTime ObservedAtUtc => File.ObservedAtUtc;
    public string? ClientSideApplicationId => File.ClientSideApplicationId;
    public string? HtmlFileType => File.HtmlFileType;
    public string? FileType => File.FileType;
    public string? BSN => File.BSN;
    public bool? WikiFieldHasValue => File.WikiFieldHasValue;
    public string? ItemMetadataReadState => File.ItemMetadataReadState;
    public string ItemMetadataFieldStatesJson => File.ItemMetadataFieldStatesJson;
    public string? NoExecuteRaw => File.NoExecuteRaw;
    public string? NoExecuteSource => File.NoExecuteSource;
    public string? NoExecuteReadState => File.NoExecuteReadState;
    public string? AspxEvidenceJson => File.AspxEvidenceJson;
    public bool? NoExecute { get; }
    public string? PageHandler => Source == null ? Evidence?.PageHandler : Source.Handler;
    public string? SourceSha256 => Source?.Sha256 ?? Evidence?.SourceSha256;
    public string? SourceReadState => Evidence?.SourceReadState;
    public string? HandlerResolution => Source?.Resolution ?? Evidence?.HandlerResolution;
    public string? PageDeclaration => Source == null ? Evidence?.PageDeclaration : Source.Declaration;
    public string? SourceError => Source?.Error ?? Evidence?.SourceError;
    public int? ListTemplate => Evidence?.ListTemplate;
    public bool? IsCatalog => Evidence?.IsCatalog;
    public bool? IsListView => Evidence?.IsListView;
    public bool? IsListForm => Evidence?.IsListForm;
    public string? RoleReadState => Evidence?.RoleReadState;
    public bool? Official => null;
    public string OfficialReadState => "NotAcquired";
}