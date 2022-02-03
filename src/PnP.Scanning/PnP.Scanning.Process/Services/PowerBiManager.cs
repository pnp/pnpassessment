using Spectre.Console;
using System.Diagnostics;

namespace PnP.Scanning.Process.Services
{
    internal static class PowerBiManager
    {
        internal static System.Diagnostics.Process LaunchPowerBiAsync(string reportToOpen)
        {
            AnsiConsole.MarkupLine("[gray]Opening the generated report in PowerBI client...[/]");

            ProcessStartInfo startInfo = new(reportToOpen)
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Minimized,
                CreateNoWindow = true,
            };

            System.Diagnostics.Process? powerBiProcess = System.Diagnostics.Process.Start(startInfo);

            if (powerBiProcess != null && !powerBiProcess.HasExited)
            {                
                AnsiConsole.MarkupLine($"[green]OK[/]");

                return powerBiProcess;
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]FAILED[/]");
                return null;
            }
        }

    }

}
