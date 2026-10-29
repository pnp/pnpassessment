#nullable enable
using Microsoft.EntityFrameworkCore;

namespace PnP.Scanning.Core.Storage;

/// <summary>Names and version of the stored ASPX acquisition declaration and its requested inventory scope.</summary>
internal static class ClassicAspxProjectionContract
{
    internal const string PropertyName = "AspxProjectionContract";
    internal const string ScopeModePropertyName = "AspxScopeMode";
    internal const string Version = "aspx-inventory/v1";
    internal const string HomePageOnly = "HomePageOnly";
    internal const string DeclaredFullInventory = "DeclaredFullInventory";

    /// <summary>Isolates wire names from earlier preview scans; new scans never write these property names or version.</summary>
    private static class LegacyCompatibility
    {
        internal const string PropertyName = "M1ProjectionContract";
        internal const string ScopeModePropertyName = "M1ScopeMode";
        internal const string Version = "M1/v1";
    }

    /// <summary>
    /// Reads the bounded declaration properties without changing saved scan data.
    /// Any current-family property makes that family authoritative; recognized preview versions normalize for neutral output,
    /// but missing or unknown current markers never borrow a legacy marker or scope to claim acquisition completeness.
    /// </summary>
    internal static async Task<StoredConfiguration> ReadAsync(ScanContext context, Guid scanId,
        CancellationToken cancellationToken = default)
    {
        string[] names =
        {
            PropertyName,
            ScopeModePropertyName,
            LegacyCompatibility.PropertyName,
            LegacyCompatibility.ScopeModePropertyName
        };
        var properties = await context.Properties.AsNoTracking()
            .Where(property => property.ScanId == scanId && names.Contains(property.Name))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        bool usesCurrentFamily = properties.Any(property =>
            property.Name == PropertyName || property.Name == ScopeModePropertyName);
        string markerName = usesCurrentFamily ? PropertyName : LegacyCompatibility.PropertyName;
        string scopeName = usesCurrentFamily ? ScopeModePropertyName : LegacyCompatibility.ScopeModePropertyName;
        string? storedVersion = properties.SingleOrDefault(property => property.Name == markerName)?.Value;
        string? scopeMode = properties.SingleOrDefault(property => property.Name == scopeName)?.Value;
        bool isRecognized = storedVersion == (usesCurrentFamily ? Version : LegacyCompatibility.Version);

        return new StoredConfiguration(isRecognized ? Version : null, scopeMode, isRecognized);
    }

    /// <summary>Normalized declaration facts for export; historical wire values are never exposed as a new contract version.</summary>
    internal sealed record StoredConfiguration(string? Version, string? ScopeMode, bool IsRecognized);
}
