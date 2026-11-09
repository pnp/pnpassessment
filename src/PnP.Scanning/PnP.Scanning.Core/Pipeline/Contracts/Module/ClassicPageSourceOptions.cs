#nullable enable

namespace PnP.Scanning.Core.Pipeline.Contracts.Module;

/// <summary>
/// Carries fixed page collection and enrichment options independently of CLI transport.
/// </summary>
internal sealed record ClassicPageSourceOptions(
    bool ExportWebPartProperties,
    bool SkipUsageInformation,
    bool SkipUserInformation,
    bool HomePageOnly,
    int AuditLogWindowDays,
    string? ExcludeSiteTemplates = null);