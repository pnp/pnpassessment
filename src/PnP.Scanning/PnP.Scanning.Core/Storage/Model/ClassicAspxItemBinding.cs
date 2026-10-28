#nullable enable
namespace PnP.Scanning.Core.Storage;

/// <summary>One normalized real item to ASPX resource relation. Shared renderers do not collapse items.</summary>
internal sealed class ClassicAspxItemBinding
{
    public Guid ScanId { get; set; }
    public string AspxKey { get; set; } = string.Empty;
    public Guid SiteCollectionId { get; set; }
    public Guid WebId { get; set; }
    public Guid ListId { get; set; }
    public int ItemId { get; set; }
    // Supplementary observation only: missing ItemGuid never prevents a real integer item-key binding.
    public Guid? ItemGuid { get; set; }
    public string ItemGuidReadState { get; set; } = "NotAttempted";
    public string? SiteUrl { get; set; }
    public string? WebUrl { get; set; }
    public string? ItemUrl { get; set; }
    public string? ItemTitle { get; set; }
    public string? ContentTypeId { get; set; }
    public ClassicAspxBindingKinds BindingKinds { get; set; }
    public string BindingProvenanceJson { get; set; } = "[]";
    public DateTime ObservedAtUtc { get; set; }
}

/// <summary>Independent acquisition reasons merged on the item/resource primary key.</summary>
[Flags]
internal enum ClassicAspxBindingKinds
{
    None = 0,
    FileSelf = 1,
    ListFormRenderer = 2,
    ContentTypeRenderer = 4,
    DocumentMetadataRenderer = 8,
    PublishingPageLayout = 16
}
