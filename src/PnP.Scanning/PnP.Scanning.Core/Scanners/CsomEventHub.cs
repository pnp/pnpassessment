using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PnP.Scanning.Core.Scanners
{
    internal sealed class CsomEventHub
    {
        /// <summary>
        /// Default constructor
        /// </summary>
        public CsomEventHub()
        {
        }

        /// <summary>
        /// Event to subscribe to get notified whenever a CSOM request is getting retried due to throttling or an error
        /// </summary>
        public Action<CsomRetryEvent> RequestRetry { get; set; }
    }
}
