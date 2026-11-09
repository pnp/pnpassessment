using Microsoft.AspNetCore.DataProtection;
using PnP.Scanning.Process.Services;
using System.CommandLine;

namespace PnP.Scanning.Process.Commands
{
    /// <summary>
    /// Composes the established CLI commands without exposing internal phase selectors.
    /// </summary>
    internal sealed class RootCommandHandler
    {
        private readonly ScannerManager processManager;
        private readonly Func<StartOptions, Task> authenticate;
        private readonly IDataProtectionProvider dataProtectionProvider;
        private readonly ConfigurationOptions configurationOptions;

        /// <summary>
        /// Initializes Root Command Handler with the supplied dependencies and fixed scope.
        /// </summary>
        public RootCommandHandler(
            ScannerManager processManagerInstance,
            IDataProtectionProvider dataProtectionProviderInstance,
            ConfigurationOptions configurationOptionsInstance,
            Func<StartOptions, Task> authenticate = null)
        {
            this.authenticate = authenticate;
            processManager = processManagerInstance;
            dataProtectionProvider = dataProtectionProviderInstance;
            configurationOptions = configurationOptionsInstance;
        }

        /// <summary>
        /// Registers the established CLI commands without exposing internal collection or analysis entry points.
        /// </summary>
        public Command Create()
        {
            RootCommand rootCommand = new RootCommand();

            rootCommand.AddCommand(new CacheCommandHandler(processManager).Create());
            rootCommand.AddCommand(new ConfigCommandHandler(processManager).Create());
            rootCommand.AddCommand(new ListCommandHandler(processManager).Create());
            rootCommand.AddCommand(new PauseCommandHandler(processManager).Create());
            rootCommand.AddCommand(new ReportCommandHandler(processManager).Create());
            rootCommand.AddCommand(new RestartCommandHandler(processManager, configurationOptions).Create());
            rootCommand.AddCommand(
                new StartCommandHandler(processManager, dataProtectionProvider, configurationOptions, authenticate)
                    .Create());
            rootCommand.AddCommand(new StatusCommandHandler(processManager).Create());
            rootCommand.AddCommand(new StopCommandHandler(processManager).Create());

            rootCommand.Description = "Microsoft 365 Assessment tool";

            return rootCommand;
        }
    }
}