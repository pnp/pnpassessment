#nullable enable
using PnP.Scanning.Core.Pipeline.Contracts.Shared;

namespace PnP.Scanning.Core.Pipeline.Contracts.List;

/// <summary>
/// Captures a candidate content query's native list identity and form declarations.
/// </summary>
internal sealed record ClassicBlogListSource(Guid ListId, string ListTitle, string ListUrl,
    string PageType, string? DisplayFormUrl, ClassicContentTypeForm[]? ContentTypeForms = null);

/// <summary>
/// Binds a captured content-type identity to its normalized display-form declaration.
/// </summary>
internal sealed record ClassicContentTypeForm(string ContentTypeId, string FormUrl);

/// <summary>
/// Retains candidate content-list scope and its explicit acquisition state.
/// </summary>
internal sealed record ClassicBlogListScopeSource(string SiteUrl, string WebUrl,
    ClassicBlogListSource[] Lists, SourceReadState State);
