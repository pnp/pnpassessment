#nullable enable
namespace PnP.Scanning.Core.Storage;

/// <summary>One observed physical ASPX file per assessment, identified only by real site-collection and file GUIDs.</summary>
internal sealed class ClassicAspxInventory
{
    public Guid ScanId { get; set; }
    public string AspxKey { get; set; } = string.Empty;
    public Guid SiteCollectionId { get; set; }
    public Guid? WebId { get; set; }
    public Guid? ListId { get; set; }
    public Guid? FolderUniqueId { get; set; }
    public Guid FileUniqueId { get; set; }
    public string? SiteUrl { get; set; }
    public string? WebUrl { get; set; }
    public string? Url { get; set; }
    public string? FileName { get; set; }
    public string? ContentTypeId { get; set; }
    public string? CustomizedPageStatus { get; set; }
    public string? SetupPath { get; set; }
    public string FileReadState { get; set; } = ClassicAspxReadStates.NotAttempted;
    public string? FileErrorCodes { get; set; }
    public string? FileErrorDetail { get; set; }
    public string ProvenanceJson { get; set; } = "[]";
    public string CoverageState { get; set; } = ClassicAspxCoverageStates.Unknown;
    public DateTime FirstObservedAtUtc { get; set; }
    public DateTime ObservedAtUtc { get; set; }

    /// <summary>Creates a tenant:v1 physical-file key from observed nonempty site-collection and file GUIDs, never from a path or observation owner.</summary>
    internal static string CreateTenantFileKey(Guid siteCollectionId, Guid fileUniqueId)
    {
        if (siteCollectionId == Guid.Empty || fileUniqueId == Guid.Empty)
        {
            throw new ArgumentException("Tenant ASPX identity requires server-observed nonempty site-collection and file GUIDs.");
        }
        return $"tenant:v1:{siteCollectionId:N}:{fileUniqueId:N}";
    }
}
