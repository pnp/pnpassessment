#nullable enable
using System.Text.Json;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;

namespace PnP.Scanning.Core.Pipeline.Contracts.Module;

/// <summary>
/// Retains existing non-page Classic component results under their captured Web scope.
/// </summary>
internal sealed record ClassicComponentSource(
    string SiteUrl,
    string WebUrl,
    SourceReadState State,
    IReadOnlyDictionary<string, JsonElement[]> Rows);