namespace PnP.Scanning.Core.Scanners
{
    internal class ClassicOptions : OptionsBase
    {
        internal bool InfoPath { get; set; }

        internal bool Workflow { get; set; }

        internal bool AzureACS { get; set; }

        internal bool SharePointAddIns { get; set; }

        internal bool Pages { get; set; }

        internal bool Lists { get; set; }

        internal bool Extensibility { get; set; }

        // Page-scan flags (see classic-page-scan merge plan §7). Additive; consumed by the
        // page scan path (T5/T10/T13). Defaults below are the no-flag behavior.

        /// <summary>Persist classic web part properties (JSON) when assessing pages.</summary>
        internal bool ExportWebPartProperties { get; set; }

        /// <summary>Skip the search-based page usage statistics (recent/lifetime views).</summary>
        internal bool SkipUsageInformation { get; set; }

        /// <summary>Skip user information lookups (e.g. page ModifiedBy).</summary>
        internal bool SkipUserInformation { get; set; }

        /// <summary>Only assess the home page of each web.</summary>
        internal bool HomePageOnly { get; set; }
    }
}
