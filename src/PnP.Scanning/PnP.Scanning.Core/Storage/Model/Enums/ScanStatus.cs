namespace PnP.Scanning.Core.Storage
{
    internal enum ScanStatus
    {
        /// <summary>
        /// Scan is queued, waiting for execution
        /// </summary>
        Queued = 1,

        /// <summary>
        /// Scan in running
        /// </summary>
        Running = 2,

        /// <summary>
        /// Scan finished
        /// </summary>
        Finished = 3,

        /// <summary>
        /// Scan is waiting to be paused
        /// </summary>
        Pausing = 4,

        /// <summary>
        /// Scan is paused
        /// </summary>
        Paused = 5,

        /// <summary>
        /// Scan was terminated
        /// </summary>
        Terminated = 6,
    }
}
