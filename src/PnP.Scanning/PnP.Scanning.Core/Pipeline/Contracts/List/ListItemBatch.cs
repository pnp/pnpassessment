#nullable enable
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Pipeline.Contracts.Page;

namespace PnP.Scanning.Core.Pipeline.Contracts.List;

/// <summary>
/// Retains one returned native ListItem and a separately acquired File association, without treating its renderer as its identity.
/// </summary>
internal sealed record ListItemFacts(
    int ListItemId,
    Dictionary<string, SourceField> Fields,
    FileItemAssociation? FileAssociation = null);

/// <summary>
/// Preserves a native ListItem.File read outcome; absent original bytes remain explicitly unattempted.
/// </summary>
internal sealed record FileItemAssociation(
    SourceReadState State,
    PhysicalFileFacts? FileFacts,
    string ContentReadState = "NotAttempted");

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