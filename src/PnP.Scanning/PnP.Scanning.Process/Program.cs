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

            if (args.Length > 0 && (args[0].Equals("orchestrator", StringComparison.InvariantCultureIgnoreCase) ||
                                    args[0].Equals("executor", StringComparison.InvariantCultureIgnoreCase)))
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
                bool runningOrchestrator = false;

                if (args[0].Equals("orchestrator", StringComparison.InvariantCultureIgnoreCase))
                {
                    runningOrchestrator = true;
                }

                if (runningOrchestrator)
                {
                    // Get port on which the orchestrator has to listen
                    int orchestratorPort = ProcessManager.DefaultOrchestratorPort;
                    if (args.Length >= 2)
                    {
                        if (int.TryParse(args[1], out int providedPort))
                        {
                            orchestratorPort = providedPort;
                        }
                    }

                    // Add and configure needed services
                    var host = ConfigureProcessHost(args, orchestratorPort, -1, runningOrchestrator);

                    // Register the port with the process manager as the part is passed down to the executors
                    var processManager = host.Services.GetRequiredService<ProcessManager>();
                    processManager.RegisterOrchestrator(Environment.ProcessId, orchestratorPort);

                    Console.WriteLine($"Running Orchestrator on port: {orchestratorPort}");

                    await host.RunAsync();
                }
                else
                {
                    // Get port on which the executor has to listen and port of the orchestrator to work with
                    int executorPort = ProcessManager.DefaultExecutorPort;
                    if (args.Length >= 2)
                    {
                        if (int.TryParse(args[1], out int providedPort))
                        {
                            executorPort = providedPort;
                        }
                    }

                    int orchestratorPort = ProcessManager.DefaultOrchestratorPort;
                    if (args.Length >= 3)
                    {
                        if (int.TryParse(args[2], out int providedPort))
                        {
                            orchestratorPort = providedPort;
                        }
                    }

                    // Add and configure needed services
                    var host = ConfigureProcessHost(args, orchestratorPort, executorPort, runningOrchestrator);

                    Console.WriteLine($"Running Executor on port: {executorPort}");
                    Console.WriteLine($"Using Orchestrator running on port: {orchestratorPort}");

                    await host.RunAsync();
                }
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

        private static IHost ConfigureProcessHost(string[] args, int orchestratorPort, int executorPort, bool runningOrchestrator)
        {
            return Host.CreateDefaultBuilder(args)
                  .ConfigureWebHostDefaults(webBuilder =>
                  {
                      if (runningOrchestrator)
                      {
                          webBuilder.UseStartup<Startup<Core.Orchestrator.Orchestrator>>();
                          executorPort = orchestratorPort;
                      }
                      else
                      {
                          webBuilder.UseStartup<Startup<Core.Executor.Executor>>();
                      }

                      webBuilder.ConfigureKestrel(options =>
                      {
                          options.ListenLocalhost(executorPort, listenOptions =>
                          {
                              listenOptions.Protocols = HttpProtocols.Http2;
                          });
                      });

                      webBuilder.ConfigureServices(services =>
                      {
                          if (runningOrchestrator)
                          {
                              services.AddSingleton<ProcessManager>();
                          }
                          else
                          {
                            // Inject a grpc client for talking to the orchestrator service
                            services.AddGrpcClient<Scanner.OrchestratorService.OrchestratorServiceClient>(configureClient =>
                              {
                                  configureClient.Address = new Uri($"http://localhost:{orchestratorPort}");
                              });

                          }
                      });

                  })

                  .UseConsoleLifetime()
                  .Build();
        }
    }
}