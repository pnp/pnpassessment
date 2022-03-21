using PnP.Scanning.Core.Storage;
using Serilog;

namespace PnP.Scanning.Core.Queues
{
    internal abstract class QueueBase<T>
    {
        internal QueueBase(StorageManager storageManager, CancellationToken cancellationToken)
        {
            StorageManager = storageManager;
            ParallelThreads = 1;
            CancellationToken = cancellationToken;
        }

        internal StorageManager StorageManager { get; private set; }

        internal int ParallelThreads { get; private set; }

        internal CancellationToken CancellationToken { get; private set; }

        internal void ConfigureQueue(int parallelThreads)
        {
            ParallelThreads = parallelThreads;
        }
    }
}
