namespace PnP.Scanning.Core.Discovery;

internal sealed record SharePointModeledFile(
    string UniqueId,
    string Name,
    string ServerRelativeUrl,
    string CustomizedPageStatus);
