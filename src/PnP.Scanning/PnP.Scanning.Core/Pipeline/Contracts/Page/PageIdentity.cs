#nullable enable
using System.Globalization;
using PnP.Scanning.Core.Pipeline.Contracts.Module;

namespace PnP.Scanning.Core.Pipeline.Contracts.Page;

/// <summary>
/// Distinguishes file-backed content from independent ListItem content; a shared renderer is never an item identity.
/// </summary>
internal enum PageObjectKind
{
    File,
    ListItem
}

/// <summary>
/// Identifies content by observed native keys inside the enclosing assessment, never by URL, byte digest, or renderer.
/// </summary>
internal sealed record PageIdentity(
    PageObjectKind ObjectKind,
    Guid SiteCollectionId,
    Guid? FileUniqueId = null,
    Guid? ListId = null,
    int? ListItemId = null)
{
    /// <summary>
    /// Creates a file identity without including optional backing-item metadata in its equality key.
    /// </summary>
    internal static PageIdentity File(Guid siteCollectionId, Guid fileUniqueId) =>
        new PageIdentity(PageObjectKind.File, siteCollectionId, fileUniqueId).Validated();

    /// <summary>
    /// Creates an independent item identity without using its shared display-form file.
    /// </summary>
    internal static PageIdentity Item(Guid siteCollectionId, Guid listId, int listItemId) =>
        new PageIdentity(PageObjectKind.ListItem, siteCollectionId, ListId: listId, ListItemId: listItemId).Validated();

    /// <summary>
    /// Rejects missing or mixed native identities rather than creating a plausible placeholder object.
    /// </summary>
    internal PageIdentity Validated()
    {
        if (SiteCollectionId == Guid.Empty || !Enum.IsDefined(ObjectKind) ||
            ObjectKind == PageObjectKind.File && (FileUniqueId is null || FileUniqueId == Guid.Empty ||
                                                  ListId != null || ListItemId != null) ||
            ObjectKind == PageObjectKind.ListItem && (ListId is null || ListId == Guid.Empty ||
                                                      ListItemId is null or <= 0 || FileUniqueId != null))
            throw new SnapshotIntegrityException(
                "A Page requires exactly one observed native file or independent item identity.");
        return this;
    }

    /// <summary>
    /// Returns an explicit discriminated key suitable for immutable analysis projections.
    /// </summary>
    internal string Key =>
        ObjectKind == PageObjectKind.File
            ? $"file|{SiteCollectionId:D}|{FileUniqueId:D}"
            : $"item|{SiteCollectionId:D}|{ListId:D}|{ListItemId?.ToString(CultureInfo.InvariantCulture)}";
}