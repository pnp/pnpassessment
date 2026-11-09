#nullable enable
using System.Text.Json;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;

namespace PnP.Scanning.Core.Pipeline.Contracts.Module;

internal sealed record ClassicComponentSource(string SiteUrl, string WebUrl, SourceReadState State,
    IReadOnlyDictionary<string, JsonElement[]> Rows);
