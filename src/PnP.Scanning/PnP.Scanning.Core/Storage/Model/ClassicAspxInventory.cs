#nullable enable
using System.Security.Cryptography;
using System.Text;

namespace PnP.Scanning.Core.Storage;

/// <summary>One physical ASPX identity per assessment, independent of every item that renders through it.</summary>
internal sealed class ClassicAspxInventory
{
    public Guid ScanId { get; set; }
    public string AspxKey { get; set; } = string.Empty;
    public string IdentityKind { get; set; } = ClassicAspxIdentityKinds.TenantFile;
    public int IdentityVersion { get; set; } = 1;
    public Guid? SiteCollectionId { get; set; }
    public Guid? FileUniqueId { get; set; }
    // Web and URL values describe an observation, never tenant-file identity or a platform catalog key.
    public Guid? WebId { get; set; }
    public string? SiteUrl { get; set; }
    public string? WebUrl { get; set; }
    public string? Url { get; set; }
    public string? FileName { get; set; }
    public string? PageType { get; set; }
    public string? ContentTypeId { get; set; }
    public string? CustomizedPageStatus { get; set; }
    public string? SetupPath { get; set; }
    public string FileReadState { get; set; } = "NotAttempted";
    public string? FileErrorCodes { get; set; }
    public string? FileErrorDetail { get; set; }
    public string? HandlerDeclaredType { get; set; }
    public string? HandlerNormalizedType { get; set; }
    public string? HandlerEffectiveType { get; set; }
    public string HandlerReadState { get; set; } = "NotAttempted";
    public string HandlerParseState { get; set; } = "NotAttempted";
    public string? HandlerSource { get; set; }
    public string? HandlerDefaultProvenance { get; set; }
    public string? HandlerReasonCode { get; set; }
    public string? HandlerErrorCodes { get; set; }
    public string? HandlerErrorDetail { get; set; }
    public string? CatalogSource { get; set; }
    public string? CatalogVersion { get; set; }
    public string? CatalogDigest { get; set; }
    public string? CatalogEntryKey { get; set; }
    public string ProvenanceJson { get; set; } = "[]";
    public string CoverageState { get; set; } = "Unknown";
    public DateTime FirstObservedAtUtc { get; set; }
    public DateTime ObservedAtUtc { get; set; }

    internal static string CreateTenantFileKey(Guid siteCollectionId, Guid fileUniqueId)
    {
        if (siteCollectionId == Guid.Empty || fileUniqueId == Guid.Empty)
            throw new ArgumentException("Tenant ASPX identity requires server-observed nonempty site-collection and file GUIDs.");
        return $"tenant:v1:{siteCollectionId:N}:{fileUniqueId:N}";
    }

    /// <summary>A frozen catalog deployment/entry identity. Never accepts or multiplies by a scheduled web.</summary>
    internal static string CreatePlatformKey(string catalogSource, string catalogVersion, string catalogDigest, string catalogEntryKey)
    {
        var parts = new[] { catalogSource, catalogVersion, catalogDigest, catalogEntryKey };
        if (parts.Any(p => string.IsNullOrWhiteSpace(p) || p.Any(char.IsControl)))
            throw new ArgumentException("Platform identity requires a complete frozen catalog deployment and entry.");
        if (catalogDigest.Length != 64 || catalogDigest.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("A SHA256 catalog digest is required.");
        var canonical = string.Join("\n", catalogSource, catalogVersion, catalogDigest.ToLowerInvariant(), catalogEntryKey);
        return "platform:v1:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}

internal static class ClassicAspxIdentityKinds
{
    public const string TenantFile = "TenantFile";
    public const string PlatformCatalog = "PlatformCatalog";
}
