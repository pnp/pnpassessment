using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.Queues
{
    internal abstract class QueueBase<T>
    {
        internal QueueBase(StorageManager storageManager, CancellationToken cancellationToken, string adminCenterUrl, string mySiteHostUrl)
        {
            StorageManager = storageManager;
            ParallelThreads = 1;
            CancellationToken = cancellationToken;
            AdminCenterUrl = adminCenterUrl;
            MySiteHostUrl = mySiteHostUrl;
        }

        internal StorageManager StorageManager { get; private set; }

        internal int ParallelThreads { get; private set; }

        internal CancellationToken CancellationToken { get; private set; }

        internal string AdminCenterUrl { get; private set; }

        internal string MySiteHostUrl { get; private set; }

        internal void ConfigureQueue(int parallelThreads)
        {
            ParallelThreads = parallelThreads;
        }
    }
}
