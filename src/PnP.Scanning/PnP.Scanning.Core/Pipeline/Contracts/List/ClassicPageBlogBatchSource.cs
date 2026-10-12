#nullable enable
using PnP.Scanning.Core.Pipeline.Contracts.Shared;

namespace PnP.Scanning.Core.Pipeline.Contracts.List;

/// <summary>
/// Preserves one acquired blog list batch, its typed item fields, owner identity, continuation and read state.
/// <para>Evolved from <c>Scanners/Classic/PageScanComponent.cs</c> to preserve blog batches and continuations for offline projection and restart.</para>
/// </summary>
internal sealed record ClassicPageBlogBatchSource(string SiteUrl, string WebUrl, Guid ListId, string ListTitle,
    string ListUrl, Dictionary<string, SourceField>[] Items, string? NextPage, SourceReadState State);
