using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Process.Commands;
using PnP.Scanning.Process.Services;
using Serilog;
using Serilog.Events;
using System.CommandLine;

namespace PnP.Scanning.Process
{
    internal class Program
    {
        internal static async Task Main(string[] args)
        {
            bool isCliProcess = true;

            if (args.Length > 0 && args[0].Equals("scanner", StringComparison.OrdinalIgnoreCase))
            {
                isCliProcess = false;
            }

            // Launching PnP.Scanning.Process.exe as CLI
            if (isCliProcess)
            {
                // Configure needed services
                var host = ConfigureCliHost(args);
                
                // Get ProcessManager instance from the cli executable
                var processManager = host.Services.GetRequiredService<ScannerManager>();

                if (args.Length == 0)
                {
                    ColorConsole.WriteWrappedHeader("Welcome to the PnP Scanning CLI!");
                    ColorConsole.WriteInfo("");
                    ColorConsole.WriteInfo("Enter the command you want to execute (<enter> to quit):");
                    var consoleInput = Console.ReadLine();

                    while (!string.IsNullOrEmpty(consoleInput))
                    {
                        await new RootCommandHandler(processManager).Create().InvokeAsync(consoleInput);

                        ColorConsole.WriteInfo("");
                        ColorConsole.WriteInfo("Enter the command you want to execute (<enter> to quit):");
                        consoleInput = Console.ReadLine();
                    }
                }
                else
                {
                    // Configure, parse and act on command line input
                    await new RootCommandHandler(processManager).Create().InvokeAsync(args);
                }
            }
            else
            {
                // Tracking issue
                // https://github.com/serilog/serilog-expressions/issues/60

                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmm");
                Log.Logger = new LoggerConfiguration()
                           .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
                           .Enrich.FromLogContext()
                           .WriteTo.Console()
                           .WriteTo.File($"log_{timestamp}.txt")
                           .CreateLogger();

                try
                {
                    // Launching PnP.Scanning.Process.exe as Kestrel web server to which we'll communicate via gRPC
                    // Get port on which the orchestrator has to listen
                    int orchestratorPort = ScannerManager.DefaultScannerPort;
                    if (args.Length >= 2)
                    {
                        if (int.TryParse(args[1], out int providedPort))
                        {
                            orchestratorPort = providedPort;
                        }
                    }

                    Log.Information($"Starting scanner on port {orchestratorPort}");

                    // Add and configure needed services
                    var host = ConfigureScannerHost(args, orchestratorPort);

                    Log.Information($"Started scanner on port {orchestratorPort}");

                    await host.RunAsync();
                }
                catch (Exception ex)
                {
                    Log.Fatal(ex, "Scanner terminated unexpectedly");
                }
                finally
                {
                    Log.CloseAndFlush();
                }
            }
        }


        private static IHost ConfigureCliHost(string[] args)
        {
            return Host.CreateDefaultBuilder(args)
                       .ConfigureServices(services =>
                       {
                           services.AddSingleton<ScannerManager>();
                       })
                       .UseConsoleLifetime()
                       .Build();
        }

        private static IHost ConfigureScannerHost(string[] args, int orchestratorPort)
        {
            return Host.CreateDefaultBuilder(args)
                  .UseSerilog() 
                  .ConfigureWebHostDefaults(webBuilder =>
                  {
                      webBuilder.UseStartup<Startup<Scanner>>();

                      webBuilder.ConfigureKestrel(options =>
                      {
                          options.ListenLocalhost(orchestratorPort, listenOptions =>
                          {
                              listenOptions.Protocols = HttpProtocols.Http2;
                          });
                      });

                      webBuilder.ConfigureServices(services =>
                      {
                          services.AddSingleton<StorageManager>();
                          services.AddSingleton<ScanManager>();
                          services.AddTransient<SiteEnumerationManager>();
                      });

                  })

                  .UseConsoleLifetime()
                  .Build();
        }
    }
}
