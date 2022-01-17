using Microsoft.Extensions.Logging;
using PnP.Scanning.Core.Scanners;
using System.Threading.Tasks.Dataflow;

namespace PnP.Scanning.Core.Queues
{
    internal sealed class WebQueue : QueueBase<WebQueue>
    {
        // Queue containting the tasks to process
        private ActionBlock<WebQueueItem>? websToScan;

        private readonly ILoggerFactory loggerFactory;

        public WebQueue(ILoggerFactory loggerFactory): base(loggerFactory)
        {
            this.loggerFactory = loggerFactory;
        }

        internal async Task EnqueueAsync(WebQueueItem web)
        {
            if (websToScan == null)
            {
                var executionDataflowBlockOptions = new ExecutionDataflowBlockOptions()
                {
                    SingleProducerConstrained = true,
                    MaxDegreeOfParallelism = ParallelThreads,
                };

                // Configure the site collection scanning queue
                websToScan = new ActionBlock<WebQueueItem>(async (web) => await ProcessWebAsync(web)
                                                                , executionDataflowBlockOptions);
            }

            // Send the request into the queue
            await websToScan.SendAsync(web);
        }

        internal void WaitForCompletion()
        {
            if (websToScan != null)
            {
                websToScan.Complete();
                websToScan.Completion.Wait();
            }
        }

        private async Task ProcessWebAsync(WebQueueItem web)
        {
            ScannerBase? scanner = null;
            if (web.OptionsBase is TestOptions testOptions)
            {
                scanner = new TestScanner(loggerFactory, web.WebUrl, testOptions);
            }

            if (scanner != null)
            {
                await scanner.ExecuteAsync();
            }
            else
            {
                throw new Exception("Unknown options class specified");
            }
        }
        
    }
}
