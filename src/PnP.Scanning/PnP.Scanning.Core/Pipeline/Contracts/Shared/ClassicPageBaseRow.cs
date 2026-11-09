namespace PnP.Scanning.Core.Pipeline.Contracts.Shared;

/// <summary>
/// Binds detached Classic results to assessment, site, and Web scope.
/// </summary>
internal class ClassicPageBaseRow
{
    public Guid ScanId { get; set; }
    public string SiteUrl { get; set; }
    public string WebUrl { get; set; }
}
