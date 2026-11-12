using PnP.Scanning.Core.ClassicPages.Contracts.Shared;

#nullable enable


namespace PnP.Scanning.Core.ClassicPages.Contracts.Page;

/// <summary>
/// Preserves a layout declaration, observed customization status, and acquisition state.
/// </summary>
internal sealed record ClassicPublishingLayoutSource(
    string Url,
    string? CustomizedPageStatusRaw,
    string? CustomizedPageStatus,
    SourceReadState State);