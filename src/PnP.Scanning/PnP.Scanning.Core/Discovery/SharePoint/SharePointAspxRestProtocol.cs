namespace PnP.Scanning.Core.Discovery;

/// <summary>Names from the SharePoint REST list, content-type, item and file JSON contracts.</summary>
internal static class SharePointAspxRestFields
{
    internal const string Id = "Id";
    internal const string Title = "Title";
    internal const string File = "File";
    internal const string UniqueId = "UniqueId";
    internal const string Name = "Name";
    internal const string ServerRelativeUrl = "ServerRelativeUrl";
    internal const string ItemGuid = "GUID";
    internal const string FileRef = "FileRef";
    internal const string ContentTypeId = "ContentTypeId";
    internal const string ContentTypeStringValue = "StringValue";
    internal const string DisplayFormUrl = "DisplayFormUrl";
    internal const string DefaultDisplayFormUrl = "DefaultDisplayFormUrl";
    internal const string PublishingPageLayout = "PublishingPageLayout";
    internal const string Url = "Url";
}

/// <summary>Exact, bounded collection queries used by native ASPX acquisition.</summary>
internal static class SharePointAspxRestQueries
{
    internal const string ListsSelect = "Id,Title,BaseType,BaseTemplate,Hidden,IsCatalog,DefaultDisplayFormUrl,RootFolder/ServerRelativeUrl";
    internal const string Lists = "_api/web/lists?$select=" + ListsSelect + "&$expand=RootFolder&$top=200";

    /// <summary>Builds the list-local content-type metadata query without changing the server's inherited-ID semantics.</summary>
    internal static string ForContentTypes(Guid listId)
    {
        return $"_api/web/lists(guid'{listId:D}')/ContentTypes?$select=Id,DisplayFormUrl&$top=200";
    }

    /// <summary>Builds the all-item query; the wildcard keeps optional publishing fields out of generic-list validation errors.</summary>
    internal static string ForItems(Guid listId)
    {
        return $"_api/web/lists(guid'{listId:D}')/items?$select=*,File/UniqueId,File/Name,File/ServerRelativeUrl&$expand=File&$top=200";
    }
}
