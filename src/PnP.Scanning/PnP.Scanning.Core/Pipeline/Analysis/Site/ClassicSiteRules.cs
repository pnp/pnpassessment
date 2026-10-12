namespace PnP.Scanning.Core.Pipeline.Analysis.Site;

/// <summary>
/// Classifies site templates for Classic Pages summaries and remediation codes.
/// <para>Evolved from <c>Scanners/Classic/ClassicScanner.cs</c> to share site classification with offline analysis without depending on the scanner.</para>
/// </summary>
internal static class ClassicSiteRules
{
        /// <summary>
        /// Maps a web template to modern, publishing, blog or other site categories.
        /// <para>Evolved from <c>Scanners/Classic/ClassicScanner.cs</c> to share site classification with offline analysis without depending on the scanner.</para>
        /// </summary>
        internal static SiteType GetSiteType(string webTemplate)
        {
            return webTemplate.ToUpper() switch
            {
                // Modern Communication site or Topic Center
                "SITEPAGEPUBLISHING#0" => SiteType.Communication,
                // Modern team site without group
                "STS#3" => SiteType.Modern,
                // Modern team site with group
                "GROUP#0" => SiteType.Modern,
                // Microsoft Syntex Content Center
                "CONTENTCTR#0" => SiteType.Modern,
                // Site linked to Team channel, version 1
                "TEAMCHANNEL#0" => SiteType.Modern,
                // Site linked to Team channel, version 2
                "TEAMCHANNEL#1" => SiteType.Modern,
                // Tenant Admin Center site
                "TENANTADMIN#0" => SiteType.Modern,
                // Publishing portal
                "BLANKINTERNETCONTAINER#0" => SiteType.Publishing,
                // Publishing site
                "CMSPUBLISHING#0" => SiteType.Publishing,
                // Publishing site
                "BLANKINTERNET#0" => SiteType.Publishing,
                // Publishing site with workflow
                "BLANKINTERNET#2" => SiteType.Publishing,
                // Blog
                "BLOG#0" => SiteType.Blog,
                // Everything else
                _ => SiteType.Classic,
            };
        }

}
