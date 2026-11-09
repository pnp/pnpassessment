#nullable enable
using PnP.Scanning.Core.Pipeline.Contracts.Shared;

namespace PnP.Scanning.Core.Pipeline.Contracts.List;

internal sealed record ClassicBlogListSource(Guid ListId, string ListTitle, string ListUrl,
    string PageType, string? DisplayFormUrl, ClassicContentTypeForm[]? ContentTypeForms = null);

internal sealed record ClassicContentTypeForm(string ContentTypeId, string FormUrl);

internal sealed record ClassicBlogListScopeSource(string SiteUrl, string WebUrl,
    ClassicBlogListSource[] Lists, SourceReadState State);
