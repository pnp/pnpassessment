using Spectre.Console;
using System.Diagnostics;

namespace PnP.Scanning.Process.Services
{
    internal static class PowerBiManager
    {
        internal static System.Diagnostics.Process LaunchPowerBiAsync(string reportToOpen)
        {
            AnsiConsole.MarkupLine("[gray]Opening the generated report in Power BI Desktop...[/]");

            ProcessStartInfo startInfo = new(reportToOpen)
            {
                UseShellExecute = true,
            };

            using (System.Diagnostics.Process powerBiProcess = System.Diagnostics.Process.Start(startInfo))
            {

                if (powerBiProcess != null && !powerBiProcess.HasExited)
                {
                    AnsiConsole.MarkupLine($"[green]OK[/]");

                    return powerBiProcess;
                }
                else
                {
                    AnsiConsole.MarkupLine($"[red]FAILED[/]");
                    AnsiConsole.MarkupLine("[red]Please verify you've Power BI Desktop installed (https://aka.ms/pbidesktopstore)[/]");
                    return null;
                }
            }
        }

    }

}
