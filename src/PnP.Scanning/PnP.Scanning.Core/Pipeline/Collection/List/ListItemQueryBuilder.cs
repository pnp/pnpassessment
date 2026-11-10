using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Pipeline.Analysis.Page;

namespace PnP.Scanning.Core.Pipeline.Collection.List;

/// <summary>
/// Builds bounded CAML queries for native items, ASPX metadata, and pointpub content.
/// </summary>
internal static class ListItemQueryBuilder
{
    /// <summary>
    /// Builds a paged CAML query for exact items or explicitly selected content evidence.
    /// </summary>
    internal static string Create(
        List<string> extraFields,
        bool filterOnAspxFiles = true,
        int? itemId = null,
        bool skipUserInformation = false,
        bool filterOnPointPublishingFiles = false)
    {
        string extraViewFields = "";
        string filter = "";

        if (extraFields.Count > 0)
        {
            foreach (string field in extraFields)
            {
                extraViewFields = $"{extraViewFields}<FieldRef Name='{field}' />";
            }
        }

        if (itemId.HasValue)
        {
            filter =
                $"<Query><Where><Eq><FieldRef Name='ID' /><Value Type='Counter'>{itemId.Value}</Value></Eq></Where></Query>";
        }
        else if (filterOnPointPublishingFiles)
        {
            filter = "<Query><Where><Eq><FieldRef Name='File_x0020_Type' />" +
                     "<Value Type='Text'>pointpub</Value></Eq></Where></Query>";
        }
        else if (filterOnAspxFiles)
        {
            filter = $@"
                      <Query>
                        <Where>
                          <Contains>
                            <FieldRef Name='File_x0020_Type'/>
                            <Value Type='text'>aspx</Value>
                          </Contains>
                        </Where>
                      </Query>";
        }

        return $@"
            <View Scope='RecursiveAll'>
              <ViewFields>
                <FieldRef Name='ID' />
                <FieldRef Name='{ClassicPageRules.ContentTypeIdField}' />
                <FieldRef Name='{ClassicPageRules.FileRefField}' />
                <FieldRef Name='{ClassicPageRules.FileLeafRefField}' />
                <FieldRef Name='{ClassicPageRules.FileTypeField}' />
                <FieldRef Name='{ClassicPageRules.ModifiedField}' />
                {(skipUserInformation ? string.Empty : $"<FieldRef Name='{ClassicPageRules.ModifiedByField}' />")}
                <FieldRef Name='{ClassicPageRules.CreatedField}' />
                <FieldRef Name='{ClassicPageRules.TitleField}' />
                <FieldRef Name='{ClassicPageRules.BSNField}' />
                {extraViewFields}
              </ViewFields>
              {filter}
              <OrderBy Override='TRUE'><FieldRef Name= 'ID' Ascending= 'FALSE' /></OrderBy>
              <RowLimit Paged='TRUE'>{(itemId.HasValue ? 2 : 1000)}</RowLimit>
            </View>";
    }
}