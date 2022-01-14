using Microsoft.Extensions.Logging;

namespace PnP.Scanning.Core.Scanners
{
    internal sealed class TestScanner: ScannerBase
    {
        internal TestScanner(ILoggerFactory loggerFactory, string webUrl, TestOptions options) : base(loggerFactory.CreateLogger<TestScanner>())
        {
            WebUrl = webUrl;
            Options = options;
        }

        internal string WebUrl { get; set; }

        internal TestOptions Options { get; set; }

        internal async override Task ExecuteAsync()
        {
            LogWarning($"Started for {WebUrl} ThreadId : {Environment.CurrentManagedThreadId}");
            int delay = new Random().Next(500, 1000);
            await Task.Delay(delay);

            LogWarning($"Step 1 Delay {WebUrl} ThreadId : {Environment.CurrentManagedThreadId}");
            delay = new Random().Next(500, 1000);
            await Task.Delay(delay);

            LogWarning($"Step 2 Delay {WebUrl} ThreadId : {Environment.CurrentManagedThreadId}");
            delay = new Random().Next(500, 1000);
            await Task.Delay(delay);

            LogWarning($"Step 3 Delay {WebUrl} ThreadId : {Environment.CurrentManagedThreadId}");
        }
    }
}
