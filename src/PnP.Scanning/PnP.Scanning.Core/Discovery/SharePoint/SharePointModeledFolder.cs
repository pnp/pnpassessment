namespace PnP.Scanning.Core.Discovery;

internal sealed record SharePointModeledFolder(
    string UniqueId,
    string Name,
    string ServerRelativeUrl);
