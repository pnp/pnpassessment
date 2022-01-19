using PnP.Scanning.Core.Services;
using PnP.Scanning.Process.Services;
using System.CommandLine;

namespace PnP.Scanning.Process.Commands
{
    internal sealed class RootCommandHandler
    {
        private readonly ScannerManager processManager;

        public RootCommandHandler(ScannerManager processManagerInstance)
        {
            processManager = processManagerInstance;
        }

        public Command Create()
        {
            var rootCommand = new RootCommand();

            rootCommand.AddCommand(new ListCommandHandler().Create());
            rootCommand.AddCommand(new StartCommandHandler(processManager).Create());
            rootCommand.AddCommand(new StopCommandHandler(processManager).Create());
            rootCommand.AddCommand(new StatusCommandHandler(processManager).Create());

            rootCommand.Description = "Microsoft 365 Scanner";

            return rootCommand;
        }
    }
}
