#nullable enable
using Microsoft.EntityFrameworkCore;

namespace PnP.Scanning.Core.Storage;

/// <summary>Names and version of the physical ASPX acquisition declaration and requested inventory scope.</summary>
internal static class ClassicAspxProjectionContract
{
    internal const string PropertyName = "AspxProjectionContract";
    internal const string ScopeModePropertyName = "AspxScopeMode";
    internal const string Version = "aspx-inventory/v1";
    internal const string HomePageOnly = "HomePageOnly";
    internal const string DeclaredFullInventory = "DeclaredFullInventory";

    /// <summary>Reads only the current declaration family without writes, backfill, or borrowing authority from unsupported preview properties.</summary>
    internal static async Task<StoredConfiguration> ReadAsync(ScanContext context, Guid scanId,
        CancellationToken cancellationToken = default)
    {
        var properties = await context.Properties.AsNoTracking()
            .Where(property => property.ScanId == scanId &&
                (property.Name == PropertyName || property.Name == ScopeModePropertyName))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var version = properties.SingleOrDefault(property => property.Name == PropertyName)?.Value;
        var scope = properties.SingleOrDefault(property => property.Name == ScopeModePropertyName)?.Value;
        var recognized = version == Version;
        return new StoredConfiguration(recognized ? Version : null, scope, recognized);
    }

    /// <summary>Read-only declaration facts; recognition never asserts complete tenant acquisition.</summary>
    internal sealed record StoredConfiguration(string? Version, string? ScopeMode, bool IsRecognized);
}
