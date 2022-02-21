namespace PnP.Scanning.Core.Scanners
{
    internal sealed class CsomRetryEvent
    {
        internal CsomRetryEvent(Guid scanId, int httpStatusCode, int waitTime, Exception exception)
        {
            ScanId = scanId;
            HttpStatusCode = httpStatusCode;
            WaitTime = waitTime;
            Exception = exception;
        }

        public Guid ScanId { get; private set; }
 
        /// <summary>
        /// Http status code for the retried request
        /// </summary>
        public int HttpStatusCode { get; private set; }

        /// <summary>
        /// Wait before the next try in seconds
        /// </summary>
        public int WaitTime { get; private set; }

        /// <summary>
        /// SocketException that triggered the retry
        /// </summary>
        public Exception Exception { get; private set; }
        
    }
}
