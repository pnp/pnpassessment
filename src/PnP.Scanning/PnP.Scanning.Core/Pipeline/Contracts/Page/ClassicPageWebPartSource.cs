#nullable enable
using PnP.Scanning.Core.Pipeline.Contracts.Shared;

namespace PnP.Scanning.Core.Pipeline.Contracts.Page;

/// <summary>
/// Preserves one original web part definition, properties, export XML and export read state before offline interpretation.
/// <para>Evolved from <c>Scanners/Classic/Pages/PageWebPartExtractor.cs</c> to capture raw definitions and exports independently of web part interpretation.</para>
/// </summary>
internal sealed record ClassicPageWebPartSource(Guid Id, string? ControlId, string ZoneId, int ZoneIndex,
    string? Title, bool Hidden, bool IsClosed, string ExportMode, Dictionary<string, SourceField> Properties,
    string? ExportXml, SourceReadState ExportState);
