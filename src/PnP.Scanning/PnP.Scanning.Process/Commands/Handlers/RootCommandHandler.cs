using Microsoft.AspNetCore.DataProtection;
using PnP.Scanning.Process.Services;
using System.CommandLine;

namespace PnP.Scanning.Process.Commands
{
    internal sealed class RootCommandHandler
    {
        private readonly ScannerManager processManager;
        private readonly IDataProtectionProvider dataProtectionProvider;
        private readonly ConfigurationOptions configurationOptions;

        public RootCommandHandler(ScannerManager processManagerInstance, IDataProtectionProvider dataProtectionProviderInstance, ConfigurationOptions configurationOptionsInstance)
        {
            processManager = processManagerInstance;
            dataProtectionProvider = dataProtectionProviderInstance;
            configurationOptions = configurationOptionsInstance;
        }

        public Command Create()
        {
            var rootCommand = new RootCommand();

            rootCommand.AddCommand(new CacheCommandHandler(processManager).Create());
            rootCommand.AddCommand(new ConfigCommandHandler(processManager).Create());
            rootCommand.AddCommand(new ListCommandHandler(processManager).Create());
            rootCommand.AddCommand(new PauseCommandHandler(processManager).Create());
            rootCommand.AddCommand(new ReportCommandHandler(processManager).Create());
            rootCommand.AddCommand(new RestartCommandHandler(processManager).Create());
            rootCommand.AddCommand(new StartCommandHandler(processManager, dataProtectionProvider, configurationOptions).Create());
            rootCommand.AddCommand(new StatusCommandHandler(processManager).Create());
            rootCommand.AddCommand(new StopCommandHandler(processManager).Create());

            rootCommand.Description = "Microsoft 365 Assessment tool";

            return rootCommand;
        }
    }
}
