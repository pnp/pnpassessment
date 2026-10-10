#nullable enable
using PnP.Scanning.Core.Pipeline.Contracts.Shared;

namespace PnP.Scanning.Core.Pipeline.Contracts.Page;

internal sealed record ClassicPageWebPartSource(Guid Id, string? ControlId, string ZoneId, int ZoneIndex,
    string? Title, bool Hidden, bool IsClosed, string ExportMode, Dictionary<string, SourceField> Properties,
    string? ExportXml, SourceReadState ExportState);
