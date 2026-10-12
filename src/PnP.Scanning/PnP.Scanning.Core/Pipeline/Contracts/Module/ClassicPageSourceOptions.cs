#nullable enable

namespace PnP.Scanning.Core.Pipeline.Contracts.Module;

/// <summary>
/// Captures page collection choices that affect the saved evidence and later analysis, including user and audit exclusions.
/// <para>Evolved from <c>Scanners/Classic/ClassicOptions.cs</c> to pin page-specific settings in a detached snapshot contract.</para>
/// </summary>
internal sealed record ClassicPageSourceOptions(bool ExportWebPartProperties, bool SkipUsageInformation,
    bool SkipUserInformation, bool HomePageOnly, int AuditLogWindowDays);
