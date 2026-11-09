#nullable enable
using PnP.Scanning.Core.Pipeline.Contracts.Shared;

namespace PnP.Scanning.Core.Pipeline.Contracts.Page;

internal sealed record ClassicPublishingLayoutSource(string Url, string? CustomizedPageStatusRaw,
    string? CustomizedPageStatus, SourceReadState State);
