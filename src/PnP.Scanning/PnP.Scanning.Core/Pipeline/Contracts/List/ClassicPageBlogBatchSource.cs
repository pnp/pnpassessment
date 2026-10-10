#nullable enable
using PnP.Scanning.Core.Pipeline.Contracts.Shared;

namespace PnP.Scanning.Core.Pipeline.Contracts.List;

internal sealed record ClassicPageBlogBatchSource(string SiteUrl, string WebUrl, Guid ListId, string ListTitle,
    string ListUrl, Dictionary<string, SourceField>[] Items, string? NextPage, SourceReadState State);
