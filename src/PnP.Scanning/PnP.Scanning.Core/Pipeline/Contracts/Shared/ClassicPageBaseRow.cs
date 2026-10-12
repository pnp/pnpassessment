namespace PnP.Scanning.Core.Pipeline.Contracts.Shared;

/// <summary>
/// Supplies assessment, site and web identity fields shared by detached Classic Pages report rows.
/// <para>Evolved from <c>Storage/Model/BaseScanResult.cs</c> to preserve report fields while removing EF entities from offline analysis.</para>
/// </summary>
internal class ClassicPageBaseRow
{
    public Guid ScanId { get; set; }
    public string SiteUrl { get; set; }
    public string WebUrl { get; set; }
}
