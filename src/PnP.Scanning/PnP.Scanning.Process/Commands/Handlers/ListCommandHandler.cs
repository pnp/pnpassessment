using System.CommandLine;

namespace PnP.Scanning.Process.Commands
{
    internal sealed class ListCommandHandler
    {
        public Command Create()
        {
            var cmd = new Command("list", "Lists the all the scans");

            // Configure options for List

            return cmd;
        }
    }
}
