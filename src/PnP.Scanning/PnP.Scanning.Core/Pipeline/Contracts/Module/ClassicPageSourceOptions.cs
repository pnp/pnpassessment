#nullable enable

namespace PnP.Scanning.Core.Pipeline.Contracts.Module;

internal sealed record ClassicPageSourceOptions(bool ExportWebPartProperties, bool SkipUsageInformation,
    bool SkipUserInformation, bool HomePageOnly, int AuditLogWindowDays);
