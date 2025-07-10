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
        
        #region Cpub templates
        internal static readonly List<string> CpubTemplates = new List<string>
        {
            "BLANKINTERNETCONTAINER#0", // Publishing Portal
            "CMSPUBLISHING#0", // Publishing Site
            "BLANKINTERNET#0", // Publishing Site
            "BLANKINTERNET#1", // Press Releases Site
            "BLANKINTERNET#2", // Publishing Site with Workflow
            "ENTERWIKI#0", // Enterprise Wiki
            "SRCHCEN#0", // Enterprise Search Center
            "SPSSITES#0", // Site Directory
            "SPSNHOME#0", // News Home Site
            "PRODUCTCATALOG#0", // Product Catalog
            "SPSREPORTCENTER#0", // Report Center
            "SPSTOPIC#0" // Topic Area Template
        };
        #endregion
    }
}
