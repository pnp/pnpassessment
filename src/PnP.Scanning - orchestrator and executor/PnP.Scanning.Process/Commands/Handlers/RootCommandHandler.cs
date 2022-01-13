using PnP.Scanning.Core.Services;
using System.CommandLine;

namespace PnP.Scanning.Process.Commands
{
    internal sealed class RootCommandHandler
    {
        private readonly ProcessManager processManager;

        public RootCommandHandler(ProcessManager processManagerInstance)
        {
            processManager = processManagerInstance;
        }

        public Command Create()
        {
            var rootCommand = new RootCommand();

            rootCommand.AddCommand(new ListCommandHandler().Create());
            rootCommand.AddCommand(new StartCommandHandler(processManager).Create());

            rootCommand.Description = "Microsoft 365 Scanner";

            return rootCommand;
        }
    }
}
