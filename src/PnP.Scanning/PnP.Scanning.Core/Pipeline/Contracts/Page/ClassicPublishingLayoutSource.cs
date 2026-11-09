#nullable enable
using PnP.Scanning.Core.Pipeline.Contracts.Shared;

namespace PnP.Scanning.Core.Pipeline.Contracts.Page;

/// <summary>
/// Preserves a layout declaration, observed customization status, and acquisition state.
/// </summary>
internal sealed record ClassicPublishingLayoutSource(string Url, string? CustomizedPageStatusRaw,
    string? CustomizedPageStatus, SourceReadState State);
