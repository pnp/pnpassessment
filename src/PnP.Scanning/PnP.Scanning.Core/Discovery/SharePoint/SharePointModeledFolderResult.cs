namespace PnP.Scanning.Core.Discovery;

internal sealed record SharePointModeledFolderResult(
    DiscoveryTerminalOutcome Outcome,
    string FolderUniqueId,
    string FolderServerRelativeUrl,
    IReadOnlyList<SharePointModeledFolder> Folders,
    IReadOnlyList<SharePointModeledFile> Files,
    string Provider,
    string Operation,
    string ErrorCode,
    string EvidenceRef,
    DateTimeOffset ReceivedAtUtc);
