using Serilog;

namespace PnP.Scanning.Core.Queues
{
    internal abstract class QueueBase<T>
    {
        internal QueueBase()
        {
            ParallelThreads = 1;
        }

        internal int ParallelThreads { get; set; }

        internal void ConfigureQueue(int parallelThreads)
        {
            Log.Information("Configuring {ParallelThreads} parallel threads for this queue", parallelThreads);
            ParallelThreads = parallelThreads;
        }
    }
}
