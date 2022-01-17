using Microsoft.Extensions.Logging;

namespace PnP.Scanning.Core.Scanners
{
    internal abstract class ScannerBase
    {
        internal ScannerBase(ILogger logger)
        {
            Logger = logger;
        }

        internal ILogger Logger { get; set; }

#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
        internal virtual async Task ExecuteAsync()
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
        {
        }

        internal void LogWarning(string? message)
        {
            if (Logger != null && message != null)
            {
                Logger.LogWarning(message);
            }
        }
    }
}
