using Grpc.Core;
using Microsoft.AspNetCore.DataProtection;
using PnP.Core.Services;
using PnP.Scanning.Core;
using PnP.Scanning.Core.Authentication;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Process.Services;
using Spectre.Console;
using System.CommandLine;
using System.CommandLine.Help;

namespace PnP.Scanning.Process.Commands
{
    internal class StartCommandHandler
    {
        private readonly ScannerManager processManager;
        private readonly IDataProtectionProvider dataProtectionProvider;
        private readonly ConfigurationOptions configurationOptions;

        private Command cmd;
        private Option<Mode> modeOption;
        private Option<string> tenantOption;
        private Option<List<string>> sitesListOption;
        private Option<FileInfo> sitesFileOption;
        private Option<AuthenticationMode> authenticationModeOption;
        private Option<Guid> applicationIdOption;
        private Option<string> tenantIdOption;
        private Option<string> certPathOption;
        private Option<FileInfo> certPfxFileInfoOption;
        private Option<string> certPfxFilePasswordOption;
        private Option<int> threadsOption;

        // PER SCAN COMPONENT: add scan component specific options here
        private Option<bool> syntexFullOption;
        private Option<bool> workflowAnalyzeOption;

#if DEBUG
        // Specific options for the test handler
        private Option<int> testNumberOfSitesOption;
#endif

        public StartCommandHandler(ScannerManager processManagerInstance, IDataProtectionProvider dataProtectionProviderInstance, ConfigurationOptions configurationOptionsInstance)
        {
            processManager = processManagerInstance;
            dataProtectionProvider = dataProtectionProviderInstance;
            configurationOptions = configurationOptionsInstance;

            cmd = new Command("start", "Starts a new Microsoft 365 Assessment");

            // Configure the options for the start command

            #region Scan scope

            // Scanner mode
            modeOption = new(
                name: $"--{Constants.StartMode}",
                getDefaultValue: () => Mode.Syntex,
                description: "Assessment mode"
                )
            {
                IsRequired = true,
            };
            cmd.AddOption(modeOption);

            tenantOption = new(
                name: $"--{Constants.StartTenant}",
                description: "Name of the tenant that will be assessed (e.g. contoso.sharepoint.com)")
            {
                IsRequired = true
            };
            cmd.AddOption(tenantOption);

            sitesListOption = new(
                name: $"--{Constants.StartSitesList}",
                parseArgument: (result) =>
                {
                    // https://github.com/dotnet/command-line-api/issues/1287
                    var siteFile = result.FindResultFor(sitesFileOption);
                    if (siteFile != null)
                    {
                        result.ErrorMessage = $"the --{Constants.StartSitesList} option is mutually exclusive with the --{Constants.StartSitesFile} option";
                        return null;
                    }

                    return result.Tokens.Select(t => t.Value).ToList();
                },
                description: "List with site collections to assess")
            {
                IsRequired = false
            };
            cmd.AddOption(sitesListOption);

            sitesFileOption = new(
                name: $"--{Constants.StartSitesFile}",
                parseArgument: (result) =>
                {
                    var siteList = result.FindResultFor(sitesListOption);
                    if (siteList != null)
                    {
                        result.ErrorMessage = $"the --{Constants.StartSitesFile} option is mutually exclusive with the --{Constants.StartSitesList} option";
                        return null;
                    }

                    return new FileInfo(result.Tokens[0].Value);
                },
                description: "File containing a list of site collections to assess")
            {
                IsRequired = false
            };

            sitesFileOption.ExistingOnly();

            cmd.AddOption(sitesFileOption);
            #endregion

            #region Scan authentication

            // Authentication mode
            authenticationModeOption = new(
                    name: $"--{Constants.StartAuthMode}",
                    getDefaultValue: () => AuthenticationMode.Interactive,
                    description: "Authentication mode used for the Microsoft 365 Assessment")
            {
                IsRequired = true
            };

            cmd.AddOption(authenticationModeOption);

            // Application id
            applicationIdOption = new(
                name: $"--{Constants.StartApplicationId}",
                // Default application to use is the PnP Management shell application
                getDefaultValue: () => Guid.Parse("31359c7f-bd7e-475c-86db-fdb8c937548e"),
                description: "Azure AD application id to use for authenticating the Microsoft 365 Assessment")
            {
                IsRequired = true
            };
            cmd.AddOption(applicationIdOption);

            // Tenant id
            tenantIdOption = new(
                name: $"--{Constants.StartTenantId}",
                description: $"Azure tenant id to use for authenticating the Microsoft 365 Assessment. Will be automatically populated based upon the {Constants.StartTenant} value")
            {
                IsRequired = false
            };
            cmd.AddOption(tenantIdOption);

            // Certificate path
            certPathOption = new(
                name: $"--{Constants.StartCertPath}",
                parseArgument: (result) =>
                {
                    // https://github.com/dotnet/command-line-api/issues/1287
                    var authenticationMode = result.FindResultFor(authenticationModeOption);
                    if (authenticationMode != null && authenticationMode.GetValueOrDefault<AuthenticationMode>() != AuthenticationMode.Application)
                    {
                        result.ErrorMessage = $"--{Constants.StartCertPath} can only be used with --{Constants.StartAuthMode} application";
                        return "";
                    }

                    return result.Tokens[0].Value;
                },
                description: "Path to stored certificate in the form of StoreName|StoreLocation|Thumbprint. E.g. My|LocalMachine|3FG496B468BE3828E2359A8A6F092FB701C8CDB1")
            {
                IsRequired = false,
            };

            certPathOption.AddValidator(val =>
            {
                // Custom validation of the provided option input 
                string input = val.GetValueOrDefault<string>();
                if (input != null && input.Split("|", StringSplitOptions.RemoveEmptyEntries).Length == 3)
                {
                    return "";
                }
                else
                {
                    return $"Invalid --{Constants.StartCertPath} value";
                }
            });
            cmd.AddOption(certPathOption);

            // Certificate PFX file
            certPfxFileInfoOption = new(
                name: $"--{Constants.StartCertFile}",
                parseArgument: (result) =>
                {
                    var authenticationMode = result.FindResultFor(authenticationModeOption);
                    if (authenticationMode != null && authenticationMode.GetValueOrDefault<AuthenticationMode>() != AuthenticationMode.Application)
                    {
                        result.ErrorMessage = $"--{Constants.StartCertPath} can only be used with --{Constants.StartAuthMode} application";
                        return null;
                    }

                    return new FileInfo(result.Tokens[0].Value);
                },
                description: "Path to certificate PFX file"
                )
            {
                IsRequired = false,
            };

            certPfxFileInfoOption.ExistingOnly();
            cmd.AddOption(certPfxFileInfoOption);

            // Certificate PFX file password
            certPfxFilePasswordOption = new(
                name: $"--{Constants.StartCertPassword}",
                parseArgument: (result) =>
                {
                    var authenticationMode = result.FindResultFor(authenticationModeOption);
                    if (authenticationMode != null && authenticationMode.GetValueOrDefault<AuthenticationMode>() != AuthenticationMode.Application)
                    {
                        result.ErrorMessage = $"--{Constants.StartCertPassword} can only be used with --{Constants.StartAuthMode} application";
                        return "";
                    }

                    return result.Tokens[0].Value;
                },
                description: "Password for the certificate PFX file")
            {
                IsRequired = false
            };
            cmd.AddOption(certPfxFilePasswordOption);

            // Application id
            threadsOption = new(
                name: $"--{Constants.StartThreads}",
                // Default application to use the logical processor threads, with a maximum of 10
                getDefaultValue: () => Math.Min(Environment.ProcessorCount, 10),
                description: "Number of parallel assessment threads to use")
            {
                IsRequired = true
            };
            cmd.AddOption(threadsOption);


            #endregion

            #region Scan component specific handlers
            // PER SCAN COMPONENT: implement scan component specific options
            syntexFullOption = new(
                name: $"--{Constants.StartSyntexFull}",
                getDefaultValue: () => false,
                description: "Perform a full Syntex assessment, requires Sites.FullControl.All when using application permissions")
            {
                IsRequired = false
            };
            cmd.AddOption(syntexFullOption);

            workflowAnalyzeOption = new(
                name: $"--{Constants.StartWorkflowAnalyze}",
                getDefaultValue: () => true,
                description: "Perform workflow analysis")
            {
                IsRequired = false
            };
            cmd.AddOption(workflowAnalyzeOption);

#if DEBUG
            testNumberOfSitesOption = new(
                name: $"--{Constants.StartTestNumberOfSites}",
                parseArgument: (result) =>
                {
                    var mode = result.FindResultFor(modeOption);
                    if (mode != null && mode.GetValueOrDefault<Mode>() != Mode.Test)
                    {
                        result.ErrorMessage = $"--{Constants.StartTestNumberOfSites} can only be used with --{Constants.StartMode} test";
                        return 10;
                    }

                    int numberOfSites = int.Parse(result.Tokens[0].Value);

                    // Set default value if needed
                    if (numberOfSites <= 0)
                    {
                        numberOfSites = 10;
                    }

                    return numberOfSites;
                },
                description: "Number of site collections to emulate for dummy assessment")
            {
                IsRequired = false
            };
            testNumberOfSitesOption.SetDefaultValue(10);
            cmd.AddOption(testNumberOfSitesOption);
#endif

            #endregion

        }

        /// <summary>
        /// https://github.com/dotnet/command-line-api/blob/main/docs/model-binding.md#more-complex-types
        /// </summary>
        /// <returns></returns>
        public Command Create()
        {
            // Binder approach as that one can handle an unlimited number of command line arguments
            var startBinder = new StartBinder(modeOption, tenantOption, sitesListOption, sitesFileOption,
                                              authenticationModeOption, applicationIdOption, tenantIdOption, certPathOption, certPfxFileInfoOption, certPfxFilePasswordOption, threadsOption
                                              // PER SCAN COMPONENT: implement scan component specific options
                                              , syntexFullOption
                                              , workflowAnalyzeOption
#if DEBUG
                                              , testNumberOfSitesOption
#endif
                                              );
            cmd.SetHandler(async (StartOptions arguments) =>
            {
                await HandleStartAsync(arguments);

            }, startBinder);

            return cmd;
        }

        private async Task HandleStartAsync(StartOptions arguments)
        {
            // Auto populate the tenant id when not provided
            if (string.IsNullOrEmpty(arguments.TenantId) && !string.IsNullOrEmpty(arguments.Tenant))
            {
                var tenantId = await AuthenticationManager.GetAzureADTenantIdAsync(arguments.Tenant);
                if (tenantId != Guid.Empty)
                {
                    arguments.TenantId = tenantId.ToString();
                }
            }

            // Additional argument validation
            if (arguments.AuthMode == AuthenticationMode.Application && string.IsNullOrEmpty(arguments.CertPath) && arguments.CertFile == null)
            {
                // we need certfile or certpath
                AnsiConsole.MarkupLine($"[red]When using --authMode Application you need to either use --CertPath or --CertFile to specify the certificate to use[/]");
                AnsiConsole.MarkupLine("");

                // Show cmd help
                var helpBld = new HelpBuilder(LocalizationResources.Instance, Console.WindowWidth);
                var hc = new HelpContext(helpBld, cmd, Console.Out);
                helpBld.Write(hc);

                return;
            }

            await AnsiConsole.Status().Spinner(Spinner.Known.BouncingBar).StartAsync("Starting Microsoft 365 Assessment...", async ctx =>
            {
                // Setup client to talk to scanner
                var client = await processManager.GetScannerClientAsync();

                // Handle authentication
                Microsoft365Environment environment = Microsoft365Environment.Production;
                try
                {
                    AnsiConsole.MarkupLine("");
                    AnsiConsole.MarkupLine($"[gray]Initializing authentication[/]");

                    if (configurationOptions != null && !string.IsNullOrEmpty(configurationOptions.Environment))
                    {
                        if (Enum.TryParse(typeof(Microsoft365Environment), configurationOptions.Environment, out object parsedEnvironment))
                        {
                            environment = (Microsoft365Environment)parsedEnvironment;
                        }
                    }

                    // Initialize authentication, this will result in a local auth cache when succesfull
                    await new AuthenticationManager(dataProtectionProvider)
                                .VerifyAuthenticationAsync(arguments.Tenant, arguments.AuthMode.ToString(), environment,
                                                           arguments.ApplicationId, arguments.TenantId,
                                                           arguments.CertPath, arguments.CertFile, arguments.CertPassword,
                                                           (deviceCodeResult) =>
                                                           {
                                                               AnsiConsole.MarkupLine(deviceCodeResult.Message);

                                                               return Task.FromResult(0);
                                                           });
                }
                catch (Exception ex)
                {
                    AnsiConsole.WriteException(ex);
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500));

                // Kick off a scan
                var start = new StartRequest
                {
                    Mode = arguments.Mode.ToString(),
                    Tenant = arguments.Tenant != null ? arguments.Tenant.ToString() : "",
                    Environment = environment.ToString(),
                    SitesList = arguments.SitesList != null ? string.Join(",", arguments.SitesList) : "",
                    SitesFile = arguments.SitesFile != null ? arguments.SitesFile.FullName : "",
                    AuthMode = arguments.AuthMode.ToString(),
                    ApplicationId = arguments.ApplicationId.ToString(),
                    TenantId = arguments.TenantId != null ? arguments.TenantId : "",
                    CertPath = arguments.CertPath != null ? arguments.CertPath : "",
                    CertFile = arguments.CertFile != null ? arguments.CertFile.FullName : "",
                    CertPassword = arguments.CertPassword != null ? arguments.CertPassword : "",
                    Threads = arguments.Threads,
                    AdminCenterUrl = (configurationOptions != null && !string.IsNullOrEmpty(configurationOptions.AdminCenterUrl)) ? configurationOptions.AdminCenterUrl : "",
                    MySiteHostUrl = (configurationOptions != null && !string.IsNullOrEmpty(configurationOptions.MySiteHostUrl)) ? configurationOptions.MySiteHostUrl : "",
                };

                // PER SCAN COMPONENT: implement scan component specific options
                if (arguments.Mode == Mode.Syntex)
                {
                    start.Properties.Add(new PropertyRequest
                    {
                        Property = syntexFullOption.Name.TrimStart('-'),
                        Type = "bool",
                        Value = arguments.SyntexDeepScan.ToString(),
                    });
                }

                if (arguments.Mode == Mode.Workflow)
                {
                    start.Properties.Add(new PropertyRequest
                    {
                        Property = workflowAnalyzeOption.Name.TrimStart('-'),
                        Type = "bool",
                        Value = arguments.WorkflowAnalyze.ToString(),
                    });
                }

#if DEBUG
                if (arguments.Mode == Mode.Test)
                {
                    start.Properties.Add(new PropertyRequest
                    {
                        Property = testNumberOfSitesOption.Name.TrimStart('-'),
                        Type = "int",
                        Value = arguments.TestNumberOfSites.ToString(),
                    });
                }
#endif
                bool encounteredError = false;
                var call = client.Start(start);
                await foreach (var message in call.ResponseStream.ReadAllAsync())
                {
                    if (message.Type == Constants.MessageError)
                    {
                        AnsiConsole.MarkupLine($"[red]{message.Status}[/]");
                        encounteredError = true;
                    }
                    else if (message.Type == Constants.MessageWarning)
                    {
                        AnsiConsole.MarkupLine($"[orange3]{message.Status}[/]");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[gray]{message.Status}[/]");
                    }

                    // Add delay for an improved "visual" experience
                    await Task.Delay(TimeSpan.FromMilliseconds(500));
                }

                if (!encounteredError)
                {
                    AnsiConsole.MarkupLine("");
                    AnsiConsole.MarkupLine($"[gray]Microsoft 365 Assessment is running![/]");
                    AnsiConsole.MarkupLine($"[gray]Use the [green]status[/] command to get realtime feedback[/]");
                    AnsiConsole.MarkupLine($"[gray]Use the [green]list[/] command to an overview of all Microsoft 365 Assessments[/]");
                }

            });
        }
    }
}
