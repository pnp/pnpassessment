using Microsoft.Extensions.Logging;

namespace PnP.Scanning.Core.Queues
{
    internal abstract class QueueBase<T>
    {
        internal QueueBase(ILoggerFactory loggerFactory)
        {
            Logger = loggerFactory.CreateLogger<T>();
            ParallelThreads = 1;
        }

        internal ILogger Logger { get; set; }

        internal int ParallelThreads { get; set; }

        internal void ConfigureQueue(int parallelThreads)
        {
            ParallelThreads = parallelThreads;
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
