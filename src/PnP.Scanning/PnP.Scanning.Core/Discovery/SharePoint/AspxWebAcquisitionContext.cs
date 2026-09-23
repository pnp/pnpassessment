namespace PnP.Scanning.Core.Discovery;

internal sealed record AspxWebAcquisitionContext(
    Guid SiteCollectionId,
    Uri SiteUrl,
    Guid WebId,
    Uri WebUrl,
    string ServerRelativeUrl,
    string WebTemplateConfiguration);
