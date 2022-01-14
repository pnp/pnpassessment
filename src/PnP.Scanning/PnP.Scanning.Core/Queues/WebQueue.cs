using Microsoft.Extensions.Logging;
using System.Threading.Tasks.Dataflow;

namespace PnP.Scanning.Core.Queues
{
    internal sealed class WebQueue : QueueBase<WebQueue>
    {
        // Queue containting the tasks to process
        private ActionBlock<string>? websToScan;

        public WebQueue(ILoggerFactory loggerFactory): base(loggerFactory)
        {
        }

        internal async Task EnqueueAsync(string webUrl)
        {
            if (websToScan == null)
            {
                var executionDataflowBlockOptions = new ExecutionDataflowBlockOptions()
                {
                    MaxDegreeOfParallelism = ParallelThreads,
                };

                // Configure the site collection scanning queue
                websToScan = new ActionBlock<string>(async (webUrl) => await ProcessWebAsync(webUrl)
                                                                , executionDataflowBlockOptions);
            }

            // Send the request into the queue
            await websToScan.SendAsync(webUrl);
        }

        private async Task ProcessWebAsync(string webUrl)
        {
            LogWarning($"Started for {webUrl} ThreadId : {Environment.CurrentManagedThreadId}");
            int delay = new Random().Next(500, 1000);
            await Task.Delay(delay);

            LogWarning($"Step 1 Delay {webUrl} ThreadId : {Environment.CurrentManagedThreadId}");
            delay = new Random().Next(500, 1000);
            await Task.Delay(delay);

            LogWarning($"Step 2 Delay {webUrl} ThreadId : {Environment.CurrentManagedThreadId}");
            delay = new Random().Next(500, 1000);
            await Task.Delay(delay);

            LogWarning($"Step 3 Delay {webUrl} ThreadId : {Environment.CurrentManagedThreadId}");
        }
        
    }
}
