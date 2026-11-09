namespace PnP.Scanning.Core.Storage
{
    /// <summary>
    /// Carries persisted assessment status and site counters with pipeline ownership metadata.
    /// </summary>
    internal sealed class ScanResultFromDatabase
    {
        internal ScanResultFromDatabase(Guid scanId, ScanStatus status, int sitesToScan)
        {
            ScanId = scanId;
            Status = status;
            SiteCollectionsToScan = sitesToScan;
        }

        internal Guid ScanId { get; set; }

        internal string Mode { get; set; }
        internal bool UsesPipeline { get; set; }

        internal ScanStatus Status { get; set; }

        internal int SiteCollectionsToScan { get; set; }

        internal int SiteCollectionsQueued { get; set; }

        internal int SiteCollectionsRunning { get; set; }

        internal int SiteCollectionsFinished { get; set; }

        internal int SiteCollectionsFailed { get; set; }

        internal DateTime StartDate { get; set; }

        internal DateTime EndDate { get; set; }
    }
}