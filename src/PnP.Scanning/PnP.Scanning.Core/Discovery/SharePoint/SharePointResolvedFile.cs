namespace PnP.Scanning.Core.Discovery;

internal sealed record SharePointResolvedFile(
    DiscoveryTerminalOutcome Outcome,
    string FileUniqueId,
    string Name,
    string ServerRelativeUrl,
    string CustomizedPageStatus,
    string EvidenceRef,
    string ErrorCode = null,
    Guid? ListId = null,
    int? ListItemId = null,
    string ContentTypeId = null);
