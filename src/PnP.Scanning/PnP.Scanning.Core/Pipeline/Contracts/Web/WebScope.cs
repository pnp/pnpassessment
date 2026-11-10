namespace PnP.Scanning.Core.Pipeline.Contracts.Web;

/// <summary>
/// Binds a Web locator and its observed template to the enclosing site collection without SDK objects or parallel arrays.
/// </summary>
internal sealed record WebScope(string SiteCollectionUrl, string WebUrl, string WebTemplate);