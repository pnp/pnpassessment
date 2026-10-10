using PnP.Scanning.Core.Pipeline.Analysis;
namespace PnP.Scanning.Core.Pipeline.Collection;

internal static class ClassicPageQuery
{
    internal static string Create(List<string> extraFields, bool filterOnASPXPages = true,
        int? itemId = null, bool skipUserInformation = false)
    {
        string extraViewFields = "";
        string filter = "";

        if (extraFields.Count > 0)
        {
            foreach(var field in extraFields)
            {
                extraViewFields = $"{extraViewFields}<FieldRef Name='{field}' />";
            }
        }

        if (itemId.HasValue)
        {
            filter = $"<Query><Where><Eq><FieldRef Name='ID' /><Value Type='Counter'>{itemId.Value}</Value></Eq></Where></Query>";
        }
        else if (filterOnASPXPages)
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
