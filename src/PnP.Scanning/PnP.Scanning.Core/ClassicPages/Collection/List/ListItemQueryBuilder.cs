namespace PnP.Scanning.Core.ClassicPages.Collection.List;

/// <summary>Builds only exact backing-item metadata reads and bounded Posts content queries.</summary>
internal static class ListItemQueryBuilder
{
    /// <summary>Reads one discovered ASPX file's exact backing item without scanning its document library.</summary>
    internal static string ForFileItem(int itemId, bool skipUserInformation)
    {
        if (itemId <= 0) throw new ArgumentOutOfRangeException(nameof(itemId));
        return Build(
            [
                "ID", "ContentTypeId", "FileRef", "FileLeafRef", "File_x0020_Type", "Modified", "Created", "Title",
                "BSN",
                "WikiField", "HTML_x0020_File_x0020_Type", "ClientSideApplicationId", "NoExecute"
            ],
            skipUserInformation,
            itemId);
    }

    /// <summary>Reads independent Posts content without requesting physical file identity or applying extension heuristics.</summary>
    internal static string ForPosts(bool skipUserInformation) =>
        Build(["ID", "ContentTypeId", "Title", "Modified", "Created"], skipUserInformation, null);

    /// <summary>Uses the existing paged native item order and limits, including a sentinel for exact-item reads.</summary>
    private static string Build(IReadOnlyList<string> fields, bool skipUserInformation, int? itemId)
    {
        IEnumerable<string> selected = skipUserInformation ? fields : fields.Append("Editor");
        string viewFields = string.Concat(selected.Select(field => $"<FieldRef Name='{field}' />"));
        string filter = itemId.HasValue
            ? $"<Query><Where><Eq><FieldRef Name='ID' /><Value Type='Counter'>{itemId.Value}</Value></Eq></Where></Query>"
            : "";
        return $@"<View Scope='RecursiveAll'>
  <ViewFields>{viewFields}</ViewFields>
  {filter}
  <OrderBy Override='TRUE'><FieldRef Name='ID' Ascending='FALSE' /></OrderBy>
  <RowLimit Paged='TRUE'>{(itemId.HasValue ? 2 : 1000)}</RowLimit>
</View>";
    }
}