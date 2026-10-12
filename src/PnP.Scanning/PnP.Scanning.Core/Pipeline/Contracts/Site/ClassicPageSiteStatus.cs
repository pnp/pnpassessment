namespace PnP.Scanning.Core.Pipeline.Contracts.Site
{
    /// <summary>
    /// Defines detached site and web lifecycle values that preserve the existing report status representation.
    /// <para>Evolved from <c>Storage/Model/Enums/SiteWebStatus.cs</c> to keep status values compatible without exposing storage types to analysis.</para>
    /// </summary>
    internal enum ClassicPageSiteStatus
    {
        Queued = 1,
        Running = 2,
        Finished = 3,
        Failed = 4,
    }
}
