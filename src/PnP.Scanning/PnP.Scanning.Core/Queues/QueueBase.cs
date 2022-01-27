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

        internal int ParallelThreads { get; private set; }

        internal void ConfigureQueue(int parallelThreads)
        {
            ParallelThreads = parallelThreads;
        }
    }
}
