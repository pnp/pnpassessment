namespace PnP.Scanning.Core
{
    internal static class Constants
    {
        #region commandline
        internal const string StartMode = "mode";
        internal const string StartTenant = "tenant";
        internal const string StartEnvironment = "environment";
        internal const string StartSitesList = "siteslist";
        internal const string StartSitesFile = "sitesfile";
        internal const string StartAuthMode = "authmode";
        internal const string StartApplicationId = "applicationid";
        internal const string StartTenantId = "tenantid";
        internal const string StartCertPath = "certpath";
        internal const string StartCertFile = "certfile";
        internal const string StartCertPassword = "certpassword";
        internal const string StartThreads = "threads";

        internal const string StartSyntexFull = "syntexfull";
        internal const string StartWorkflowAnalyze = "workflowanalyze";
        internal const string StartClassicInclude = "classicinclude";
        internal const string StartTestNumberOfSites = "testnumberofsites";

        internal const string ListRunning = "running";
        internal const string ListPaused = "paused";
        internal const string ListFinished = "finished";
        internal const string ListTerminated = "terminated";

        internal const string PauseScanId = "id";
        internal const string PauseAll = "all";

        internal const string ReportMode = "mode";
        internal const string ReportDelimiter = "delimiter";
        internal const string ReportPath = "path";
        internal const string ReportOpen = "open";

        internal const string CacheClearAuthentication = "clearauthentication";
        #endregion

        #region Status
        internal const string MessageInformation = "Information";
        internal const string MessageWarning = "Warning";
        internal const string MessageError = "Error";
        #endregion

        #region History events
        internal const string EventAssessmentStatusChange = "AssessmentStatusChange";
        internal const string EventPreAssessmentStatusChange = "PreAssessmentStatusChange";
        internal const string EventPostAssessmentStatusChange = "PostAssessmentStatusChange";
        #endregion

        #region Encryption
        internal const string DataProtectorMsalCachePurpose = "MSALCache";
        internal const string DataProtectorPasswordPurpose = "Password";
        internal const string MsalCacheFile = "msalcache.bin";
        #endregion

        #region PnPContext properties
        internal const string PnPContextPropertyScanId = "ScanId";
        #endregion

        #region Classic WebParts Without Proper Property Mappings
        /// <summary>
        /// Classic SharePoint web part types that do not have proper property mappings to modern client side web parts
        /// </summary>
        internal static readonly string[] ClassicWebPartsWithoutProperMappings = new[]
        {
            // Search Web Parts (minimal or no property mappings)
            "Microsoft.Office.Server.Search.WebControls.SearchNavigationWebPart",
            "Microsoft.Office.Server.Search.WebControls.CatalogItemReuseWebPart",
            "Microsoft.Office.Server.Search.WebControls.TaxonomyRefinementScriptWebPart",
            "Microsoft.Office.Server.Search.WebControls.AdvancedSearchBox",
            "Microsoft.Office.Server.Search.WebControls.SearchBoxScriptWebPart",
            "Microsoft.Office.Server.Search.WebControls.RefinementScriptWebPart",

            // SharePoint Portal WebControls (no mappings)
            "Microsoft.SharePoint.Portal.WebControls.SiteDocuments",
            "Microsoft.SharePoint.Portal.WebControls.RSSAggregatorWebPart",
            "Microsoft.SharePoint.Portal.WebControls.SocialCommentWebPart",
            "Microsoft.SharePoint.Portal.WebControls.ProfileBrowser",
            "Microsoft.SharePoint.Portal.WebControls.TagCloudWebPart",

            // Project Site Web Parts (no mappings)
            "Microsoft.SharePoint.Portal.WebControls.ProjectSummaryWebPart",

            // KPI Web Parts (no mappings)
            "Microsoft.SharePoint.Portal.WebControls.IndicatorWebpart",
            "Microsoft.SharePoint.Portal.WebControls.KPIListWebPart",

            // Community Site Web Parts (no mappings)
            "Microsoft.SharePoint.Portal.WebControls.CommunityAdminWebPart",
            "Microsoft.SharePoint.Portal.WebControls.CommunityJoinWebPart",
            "Microsoft.SharePoint.Portal.WebControls.DashboardWebPart",
            "Microsoft.SharePoint.Portal.WebControls.AboutUsWebPart",
            "Microsoft.SharePoint.Portal.WebControls.MyMembershipWebPart",

            // OWA Web Parts (no mappings)
            "Microsoft.SharePoint.Portal.WebControls.OWAInboxPart",
            "Microsoft.SharePoint.Portal.WebControls.OWACalendarPart",
            "Microsoft.SharePoint.Portal.WebControls.OWAContactsPart",
            "Microsoft.SharePoint.Portal.WebControls.OWATasksPart",

            // Category Web Parts (no mappings)
            "Microsoft.SharePoint.Portal.WebControls.CategoryWebPart",
            "Microsoft.SharePoint.Portal.WebControls.CategoryResultsWebPart",

            // Announcements Web Parts (no mappings)
            "Microsoft.SharePoint.Portal.WebControls.AnnouncementTilesWebPart",

            // Tasks and Tools Web Parts (no mappings)
            "Microsoft.SharePoint.Portal.WebControls.TasksAndToolsWebPart",

            // Week in Pictures Web Parts (no mappings)
            "Microsoft.SharePoint.Portal.WebControls.ThisWeekInPicturesWebPart",

            // User Tasks Web Parts (no mappings)
            "Microsoft.SharePoint.WebPartPages.UserTasksWebPart",

            // Timeline Web Parts (no mappings)
            "Microsoft.SharePoint.WebPartPages.SPTimelineWebPart",

            // Taxonomy Web Parts (no mappings)
            "Microsoft.SharePoint.Taxonomy.TermProperty",

            // Business Data Web Parts (no mappings)
            "Microsoft.SharePoint.Portal.WebControls.BusinessDataListWebPart",
            "Microsoft.SharePoint.Portal.WebControls.BusinessDataFilterWebPart",
            "Microsoft.SharePoint.Portal.WebControls.BusinessDataDetailsWebPart",
            "Microsoft.SharePoint.Portal.WebControls.BusinessDataAssociationWebPart",
            "Microsoft.SharePoint.Portal.WebControls.BusinessDataActionsWebPart",
            "Microsoft.SharePoint.Portal.WebControls.BusinessDataItemBuilder",

            // Filter Web Parts (no mappings)
            "Microsoft.SharePoint.Portal.WebControls.ScorecardFilterWebPart",
            "Microsoft.SharePoint.Portal.WebControls.ApplyFiltersWebPart",
            "Microsoft.SharePoint.Portal.WebControls.DateFilterWebPart",
            "Microsoft.SharePoint.Portal.WebControls.UserContextFilterWebPart",
            "Microsoft.SharePoint.Portal.WebControls.SPSlicerTextWebPart",
            "Microsoft.SharePoint.Portal.WebControls.SpListFilterWebPart",
            "Microsoft.SharePoint.Portal.WebControls.QueryStringFilterWebPart",
            "Microsoft.SharePoint.Portal.WebControls.PageContextFilterWebPart",
            "Microsoft.SharePoint.Portal.WebControls.SPSlicerChoicesWebPart",

            // Blog Site Web Parts (no mappings)
            "Microsoft.SharePoint.WebPartPages.BlogLinksWebPart",
            "Microsoft.SharePoint.WebPartPages.BlogAdminWebPart",
            "Microsoft.SharePoint.WebPartPages.BlogMonthQuickLaunch",
            "Microsoft.SharePoint.WebPartPages.BlogYearArchive",

            // Error Web Parts (no mappings)
            "Microsoft.SharePoint.WebPartPages.ErrorWebPart",

            // App Catalog Web Parts (no mappings)
            "Microsoft.SharePoint.WebPartPages.GettingStartedWithAppCatalogSiteWebPart",

            // Document Set Web Parts (no mappings)
            "Microsoft.Office.Server.WebControls.DocIdSearchWebPart",
            "Microsoft.Office.Server.WebControls.DocumentSetPropertiesWebPart",
            "Microsoft.Office.Server.WebControls.DocumentSetContentsWebPart",

            // Analytics Web Parts (no mappings)
            "Microsoft.Office.Server.WebAnalytics.Reporting.WhatsPopularWebPart",

            // Chart Web Parts (no mappings)
            "Microsoft.Office.Server.WebControls.ChartWebPart",

            // Web Parts with empty mappings (drop without transformation)
            "Microsoft.SharePoint.WebPartPages.GettingStartedWebPart",
            "Microsoft.Office.InfoPath.Server.Controls.WebUI.BrowserFormWebPart",
            "Microsoft.SharePoint.WebPartPages.SPUserCodeWebPart",
            "Microsoft.SharePoint.WebPartPages.TitleBarWebPart",
            "Microsoft.SharePoint.Publishing.WebControls.TableOfContentsWebPart"
        };
        #endregion
    }
}
