using PnP.Scanning.Process.Services;
using Spectre.Console;
using System.CommandLine;

namespace PnP.Scanning.Process.Commands
{
    internal sealed class ConfigCommandHandler
    {
        private readonly ScannerManager processManager;

        public ConfigCommandHandler(ScannerManager processManagerInstance)
        {
            processManager = processManagerInstance;
        }

        public Command Create()
        {
            var cmd = new Command("config", "Outputs the Microsoft 365 Assessment configuration");

            // Configure options for status

            cmd.SetHandler(() => HandleStatus());

            return cmd;
        }

        private void HandleStatus()
        {
            // Create a table
            var table = new Table().BorderColor(Color.Grey);

            // Add some columns
            table.AddColumn("Setting ");
            table.AddColumn("Value");
            table.AddColumn("Default");
            table.AddColumn("appsettings.json path");

            table.AddRow("Port", ScannerManager.DefaultScannerPort.ToString(), ScannerManager.StandardScannerPort.ToString(), "CustomSettings:Port");

            AnsiConsole.Write(table);
        }
    }
}
