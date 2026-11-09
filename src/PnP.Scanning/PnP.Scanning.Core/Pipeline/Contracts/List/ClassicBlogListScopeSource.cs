#nullable enable
using PnP.Scanning.Core.Pipeline.Contracts.Shared;

namespace PnP.Scanning.Core.Pipeline.Contracts.List;

internal sealed record ClassicBlogListSource(Guid ListId, string ListTitle, string ListUrl,
    string PageType, string? DisplayFormUrl);

internal sealed record ClassicBlogListScopeSource(string SiteUrl, string WebUrl,
    ClassicBlogListSource[] Lists, SourceReadState State);
