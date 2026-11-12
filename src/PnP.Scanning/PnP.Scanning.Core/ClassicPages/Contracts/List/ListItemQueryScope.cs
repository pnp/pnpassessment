using PnP.Scanning.Core.ClassicPages.Contracts.Shared;

#nullable enable


namespace PnP.Scanning.Core.ClassicPages.Contracts.List;

/// <summary>
/// Identifies the supported independent-content acquisition scope; file metadata is read only for discovered ASPX.
/// </summary>
internal enum ListItemQueryKind
{
    Posts,
}

/// <summary>
/// Captures a native list query and observed list template independently of final content classification.
/// </summary>
internal sealed record ListItemQueryScope(
    Guid ListId,
    string ListTitle,
    string ListUrl,
    ListItemQueryKind QueryKind,
    int ListTemplate,
    string? DisplayFormUrl,
    ClassicContentTypeForm[]? ContentTypeForms = null);

/// <summary>
/// Binds an observed content-type identity to a normalized display-form declaration.
/// </summary>
internal sealed record ClassicContentTypeForm(string ContentTypeId, string FormUrl);

/// <summary>
/// Retains fixed candidate queries and their independent Web-scope acquisition outcome.
/// </summary>
internal sealed record WebListItemQueries(
    string SiteUrl,
    string WebUrl,
    ListItemQueryScope[] Queries,
    SourceReadState State);