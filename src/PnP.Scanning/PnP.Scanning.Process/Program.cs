using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PnP.Scanning.Process.Commands;
using PnP.Scanning.Core.Services;
using System.CommandLine;

namespace PnP.Scanning.Process
{
    internal class Program
    {
        internal static async Task Main(string[] args)
        {
            bool isCliProcess = true;

            if (args.Length > 0 && (args[0].Equals("scanner", StringComparison.InvariantCultureIgnoreCase)))
            {
                isCliProcess = false;
            }

            if (isCliProcess)
            {
                // Configure needed services
                var host = ConfigureCliHost(args);
                // Get ProcessManager
                var processManager = host.Services.GetRequiredService<ProcessManager>();


                if (args.Length == 0)
                {
                    Console.WriteLine("Welcome to the PnP Scanning CLI!");

                    Console.WriteLine("Enter the command you want to execute:");
                    var input = Console.ReadLine();

                    while (!string.IsNullOrEmpty(input))
                    {
                        await new RootCommandHandler(processManager).Create().InvokeAsync(input);

                        Console.WriteLine("Enter the command you want to execute:");
                        input = Console.ReadLine();
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
                // Get port on which the orchestrator has to listen
                int orchestratorPort = ProcessManager.DefaultScannerPort;
                if (args.Length >= 2)
                {
                    if (int.TryParse(args[1], out int providedPort))
                    {
                        orchestratorPort = providedPort;
                    }
                }

                // Add and configure needed services
                var host = ConfigureProcessHost(args, orchestratorPort);

                // Register the port with the process manager as the part is passed down to the executors
                var processManager = host.Services.GetRequiredService<ProcessManager>();
                processManager.RegisterScannerProcessForCli(Environment.ProcessId, orchestratorPort, host);

                Console.WriteLine($"Running scanner on port: {orchestratorPort}");

                await host.RunAsync();
            }
        }


        private static IHost ConfigureCliHost(string[] args)
        {
            return Host.CreateDefaultBuilder(args)
                       .ConfigureServices(services =>
                       {
                           services.AddSingleton<ProcessManager>();
                       })
                       .UseConsoleLifetime()
                       .Build();
        }

        private static IHost ConfigureProcessHost(string[] args, int orchestratorPort)
        {
            return Host.CreateDefaultBuilder(args)
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
                              services.AddSingleton<ProcessManager>();
                      });

                  })

                  .UseConsoleLifetime()
                  .Build();
        }
    }
}
