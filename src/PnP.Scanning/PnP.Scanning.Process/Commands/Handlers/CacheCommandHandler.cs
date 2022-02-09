using PnP.Scanning.Core;
using PnP.Scanning.Core.Authentication;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Process.Services;
using Spectre.Console;
using System.CommandLine;

namespace PnP.Scanning.Process.Commands
{
    internal sealed class CacheCommandHandler
    {
        private readonly ScannerManager processManager;

        private Command cmd;

        private Option<bool> clearOption;

        internal CacheCommandHandler(ScannerManager processManagerInstance)
        {
            processManager = processManagerInstance;

            cmd = new Command("cache", "Manages the scanner cache");

            // Scanner mode
            clearOption = new(
                name: $"--{Constants.CacheClearAuthentication}",
                getDefaultValue: () => false,
                description: "Clears the scanner authentication cache"
                )
            {
                IsRequired = false,
            };
            cmd.AddOption(clearOption);

        }

        public Command Create()
        {
            cmd.SetHandler(async (bool clearAuthentication) => 
                            { 
                                await HandleStartAsync(clearAuthentication); 
                            },
                            clearOption);

            return cmd;
        }

        private async Task HandleStartAsync(bool clearAuthentication)
        {
            if (clearAuthentication)
            {
                var cacheFile = TokenCacheManager.CacheFilePath(StorageManager.GetScannerFolder());
                if (File.Exists(cacheFile))
                {
                    File.Delete(cacheFile);
                    AnsiConsole.MarkupLine($"[gray]Authentication cache file {cacheFile} was cleared[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine($"[orange3]There was no authentication cache file {cacheFile} to clear[/]");
                }
            }

        }
    }
}
