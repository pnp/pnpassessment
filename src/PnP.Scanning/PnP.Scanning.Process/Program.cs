using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PnP.Core.Auth.Services.Builder.Configuration;
using PnP.Core.Services.Builder.Configuration;
using PnP.Scanning.Core.Authentication;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Process.Commands;
using PnP.Scanning.Process.Services;
using Serilog;
using Serilog.Events;
using Spectre.Console;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;

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

                await AnsiConsole.Status().Spinner(Spinner.Known.BouncingBar).StartAsync("Version check...", async ctx =>
                {
                    var versions = await VersionManager.LatestVersionAsync();

                    // There's a newer version to download                
                    if (!string.IsNullOrEmpty(versions.Item2))
                    {
                        AnsiConsole.WriteLine();
                        AnsiConsole.MarkupLine($"Version [yellow]{versions.Item2}[/] is available, you are currently using version {versions.Item1}");
                        AnsiConsole.WriteLine();
                        AnsiConsole.MarkupLine($"Download the latest version from [yellow]{VersionManager.newVersionDownloadUrl}[/]");
                        AnsiConsole.WriteLine();
                    }
                    else
                    {
                        AnsiConsole.WriteLine();
                        AnsiConsole.MarkupLine($"You are using the latest version {versions.Item1}");
                        AnsiConsole.WriteLine();
                    }
                });

                // Configure needed services
                var host = ConfigureCliHost(args);

                // Get ProcessManager instance from the cli executable
                var processManager = host.Services.GetRequiredService<ScannerManager>();
                var dataProtectionProvider = host.Services.GetRequiredService<IDataProtectionProvider>();
                var configurationData = host.Services.GetRequiredService<ConfigurationOptions>();

                var root = new RootCommandHandler(processManager, dataProtectionProvider, configurationData).Create();
                var builder = new CommandLineBuilder(root);
                var parser = builder.UseDefaults().Build();

                if (args.Length == 0)
                {
                    AnsiConsole.Write(new FigletText("Microsoft 365 Assessment").Centered().Color(Color.Green));
                    AnsiConsole.WriteLine("");
                    AnsiConsole.MarkupLine("Execute a command [gray](<enter> to quit)[/]: ");
                    var consoleInput = Console.ReadLine();

                    // Possible enhancement: build custom tab completion for the "console mode"
                    // To get suggestions
                    // var result = parser.Parse(consoleInput).GetCompletions();
                    // Sample to start from: https://www.codeproject.com/Articles/1182358/Using-Autocomplete-in-Windows-Console-Applications

                    while (!string.IsNullOrEmpty(consoleInput))
                    {
                        AnsiConsole.WriteLine("");
                        await parser.InvokeAsync(consoleInput);

                        AnsiConsole.WriteLine("");
                        AnsiConsole.MarkupLine("Execute a command [gray](<enter> to quit)[/]: ");
                        consoleInput = Console.ReadLine();
                    }
                }
                else
                {
                    await parser.InvokeAsync(args);
                }
            }
            else
            {
                // Use fully qualified paths as otherwise on MacOS the files end up in the wrong location
                string logFolder = Path.GetDirectoryName(Environment.ProcessPath);
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmm");
                Log.Logger = new LoggerConfiguration()
                           .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
                           .Enrich.FromLogContext()
#if DEBUG
                           .WriteTo.Console()
#endif
                           .WriteTo.File(Path.Combine(logFolder, $"log_{timestamp}.txt"))
                           // Duplicate all log entries generated for an actual scan component
                           // to a separate log file in the folder per scan
                           .WriteTo.Map("ScanId", (scanId, wt) => wt.File(Path.Combine(logFolder, scanId, $"log_{scanId}.txt")))
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

                    Log.Information($"Starting Microsoft 365 Assessment on port {orchestratorPort}");

                    // Add and configure needed services
                    var host = ConfigureScannerHost(args, orchestratorPort);

                    Log.Information($"Started Microsoft 365 Assessment on port {orchestratorPort}");

                    await host.RunAsync();
                }
                catch (Exception ex)
                {
                    Log.Fatal(ex, "Microsoft 365 Assessment terminated unexpectedly");
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
                       .ConfigureLogging((context, logging) =>
                       {
                           // Clear all previously registered providers, don't want logger output to mess with the CLI console output
                           logging.ClearProviders();
                       })
                       .ConfigureServices((context, services) =>
                       {
                           // Inject configuration data
                           var customSettings = new ConfigurationOptions();
                           context.Configuration.Bind("CustomSettings", customSettings);
                           services.AddSingleton(customSettings);

                           services.AddDataProtection();

                           services.AddSingleton<ScannerManager>();
                           services.AddTransient<AuthenticationManager>();
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

                      webBuilder.ConfigureServices((context, services) =>
                      {
                          services.AddDataProtection();

                          // Add the PnP Core SDK library
                          services.AddPnPCore(options =>
                          {
                              // Set Graph first to false to optimize performance
                              options.PnPContext.GraphFirst = false;
                              // Remove the HTTP timeout to ensure the request does not end before the throttling is over
                              options.HttpRequests.Timeout = -1;
                          });
                          services.Configure<PnPCoreOptions>(context.Configuration.GetSection("PnPCore"));
                          services.AddPnPCoreAuthentication();
                          services.Configure<PnPCoreAuthenticationOptions>(context.Configuration.GetSection("PnPCore"));

                          services.AddSingleton<StorageManager>();
                          services.AddSingleton<CsomEventHub>();
                          services.AddSingleton<ScanManager>();
                          services.AddSingleton<TelemetryManager>();
                          services.AddTransient<SiteEnumerationManager>();
                          services.AddTransient<ReportManager>();
                      });

                  })

                  .UseConsoleLifetime()
                  .Build();
        }
    }
}
