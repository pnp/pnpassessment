namespace PnP.Scanning.Core.Pipeline.Analysis.Site
{
    /// <summary>
    /// Defines the site template categories used by Classic assessment rules and summaries.
    /// <para>Evolved from <c>Scanners/Classic/SiteType.cs</c> to share site categories with offline rules and report builders.</para>
    /// </summary>
    internal enum SiteType
    {
        Modern,
        Communication,
        Classic,
        Publishing,
        Blog
    }
}
