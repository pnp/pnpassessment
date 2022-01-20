using PnP.Scanning.Core.Storage;
using Serilog;

namespace PnP.Scanning.Core.Queues
{
    internal abstract class QueueBase<T>
    {
        internal QueueBase(StorageManager storageManager)
        {
            StorageManager = storageManager;
            ParallelThreads = 1;
        }

        internal StorageManager StorageManager { get; private set; }

        internal int ParallelThreads { get; set; }

        internal void ConfigureQueue(int parallelThreads)
        {
            Log.Information("Configuring {ParallelThreads} parallel threads for this queue", parallelThreads);
            ParallelThreads = parallelThreads;
        }
    }
}
