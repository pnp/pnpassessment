using PnP.Scanning.Core.ClassicPages.Contracts.Shared;

#nullable enable


namespace PnP.Scanning.Core.ClassicPages.Contracts.List;

/// <summary>
/// Retains one returned Posts item independently of the physical file that renders it.
/// </summary>
internal sealed record ListItemFacts(int ListItemId, Dictionary<string, SourceField> Fields);

/// <summary>
/// Captures returned items, observed template, and native continuations for offline interpretation.
/// </summary>
internal sealed record ListItemBatch(
    string SiteUrl,
    string WebUrl,
    Guid ListId,
    string? ListTitle,
    string? ListUrl,
    ListItemFacts[] Items,
    string? NextPage,
    SourceReadState State,
    ListItemQueryKind QueryKind,
    int ListTemplate,
    string? DisplayFormUrl = null,
    int PageOrdinal = 0,
    string? PagingToken = null,
    ClassicContentTypeForm[]? ContentTypeForms = null);

/// <summary>
/// Detaches one list-data response before object-specific acquisition or classification.
/// </summary>
internal sealed record ListItemFieldBatch(
    Guid ListId,
    string ListTitle,
    string ListUrl,
    Dictionary<string, SourceField>[] Items,
    string? NextPage,
    SourceReadState State);