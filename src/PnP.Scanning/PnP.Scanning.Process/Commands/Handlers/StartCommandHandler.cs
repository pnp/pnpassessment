using Grpc.Core;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Process.Services;
using System.CommandLine;

namespace PnP.Scanning.Process.Commands
{
    internal class StartCommandHandler
    {
        private readonly ScannerManager processManager;

        private Command cmd;
        private Option<Mode> modeOption;
        private Option<string> tenantOption;
        private Option<Microsoft365Environment> environmentOption;
        private Option<List<string>> sitesListOption;
        private Option<FileInfo> sitesFileOption;
        private Option<AuthenticationMode> authenticationModeOption;
        private Option<Guid> applicationIdOption;
        private Option<string> certPathOption;
        private Option<FileInfo> certPfxFileInfoOption;
        private Option<string> certPfxFilePasswordOption;

        public StartCommandHandler(ScannerManager processManagerInstance)
        {
            processManager = processManagerInstance;

            cmd = new Command("start", "starts a scan");

            // Configure the options for the start command

            #region Scan scope

            // Scanner mode
            modeOption = new(
                name: "--mode",
                getDefaultValue: () => Mode.Test,
                description: "Scanner mode"
                )
            {
                IsRequired = true,
            };
            cmd.AddOption(modeOption);

            tenantOption = new(
                name: "--tenant", 
                description: "Name of the tenant that will be scanned (e.g. contoso.sharepoint.com)")
            {
                IsRequired = false
            };
            cmd.AddOption(tenantOption);

            environmentOption = new(
                name: "--environment",
                getDefaultValue: () => Microsoft365Environment.Production,
                description: "The cloud environment you're scanning")
            {
                IsRequired = false
            };
            cmd.AddOption(environmentOption);

            sitesListOption = new(
                name: "--siteslist",
                parseArgument: (result) =>
                {
                    // https://github.com/dotnet/command-line-api/issues/1287
#pragma warning disable CS8604 // Possible null reference argument.
                    var siteFile = result.FindResultFor(sitesFileOption);
#pragma warning restore CS8604 // Possible null reference argument.
                    if (siteFile != null)
                    {
                        result.ErrorMessage = "the --siteslist option is mutually exclusive with the --sitesfile option";
#pragma warning disable CS8603 // Possible null reference return.
                        return null;
#pragma warning restore CS8603 // Possible null reference return.
                    }

                    return result.Tokens.Select(t => t.Value).ToList();
                }, 
                description: "List with site collections to scan")
            {
                IsRequired = false
            };
            cmd.AddOption(sitesListOption);

            sitesFileOption = new(
                name: "--sitesfile",
                parseArgument: (result) =>
                {
                    var siteList = result.FindResultFor(sitesListOption);
                    if (siteList != null)
                    {
                        result.ErrorMessage = "the --sitesfile option is mutually exclusive with the --siteslist option";
#pragma warning disable CS8603 // Possible null reference return.
                        return null;
#pragma warning restore CS8603 // Possible null reference return.
                    }

                    return new FileInfo(result.Tokens[0].Value);
                },
                description: "File containing a list of site collections to scan")
            {
                IsRequired = false
            };

            sitesFileOption.ExistingOnly();

            cmd.AddOption(sitesFileOption);
            #endregion

            #region Scan authentication

            // Authentication mode
            authenticationModeOption = new(
                    name: "--authmode",
                    getDefaultValue: () => AuthenticationMode.Interactive,
                    description: "Authentication mode used for the scan")
            {
                IsRequired = true
            };

            cmd.AddOption(authenticationModeOption);

            // Application id
            applicationIdOption = new(
                name: "--applicationid",
                // Default application to use is the PnP Management shell application
                getDefaultValue: () => Guid.Parse("31359c7f-bd7e-475c-86db-fdb8c937548e"),
                description: "Azure AD application id to use for authenticating the scan")
            {
                IsRequired = true
            };
            cmd.AddOption(applicationIdOption);

            // Certificate path
            certPathOption = new(
                name: "--certpath",
                parseArgument: (result) =>
                {
                    // https://github.com/dotnet/command-line-api/issues/1287
                    var authenticationMode = result.FindResultFor(authenticationModeOption);
                    if (authenticationMode != null && authenticationMode.GetValueOrDefault<AuthenticationMode>() != AuthenticationMode.Application)
                    {
                        result.ErrorMessage = "--certpath can only be used with --authmode application";
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
                string? input = val.GetValueOrDefault<string>();
                if (input != null && input.Split("|", StringSplitOptions.RemoveEmptyEntries).Length == 3)
                {
                    return null;
                }
                else
                {
                    return "Invalid certpath value";
                }
            });
            cmd.AddOption(certPathOption);

            // Certificate PFX file
            certPfxFileInfoOption = new(
                name: "--certfile",
                parseArgument: (result) =>
                {
                    var authenticationMode = result.FindResultFor(authenticationModeOption);
                    if (authenticationMode != null && authenticationMode.GetValueOrDefault<AuthenticationMode>() != AuthenticationMode.Application)
                    {
                        result.ErrorMessage = "--certpath can only be used with --authmode application";
#pragma warning disable CS8603 // Possible null reference return.
                        return null;
#pragma warning restore CS8603 // Possible null reference return.
                    }

#pragma warning disable CS8604 // Possible null reference argument.
                    if (result.FindResultFor(certPfxFilePasswordOption) is { })
                    {
                        result.ErrorMessage = "using --certfile also requires using --certpassword";
#pragma warning disable CS8603 // Possible null reference return.
                        return null;
#pragma warning restore CS8603 // Possible null reference return.
                    }
#pragma warning restore CS8604 // Possible null reference argument.

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
                name: "--certpassword",
                parseArgument: (result) =>
                {
                    var authenticationMode = result.FindResultFor(authenticationModeOption);
                    if (authenticationMode != null && authenticationMode.GetValueOrDefault<AuthenticationMode>() != AuthenticationMode.Application)
                    {
                        result.ErrorMessage = "--certpassword can only be used with --authmode application";
                        return "";
                    }

                    if (result.FindResultFor(certPfxFileInfoOption) is { })
                    {
                        result.ErrorMessage = "using --certpassword also requires using --certfile";
                        return "";
                    }

                    return result.Tokens[0].Value;
                },
                description: "Password for the certificate PFX file")
            {
                IsRequired = false
            };
            cmd.AddOption(certPfxFilePasswordOption);

            #endregion

        }

        /// <summary>
        /// https://github.com/dotnet/command-line-api/blob/main/docs/model-binding.md#more-complex-types
        /// </summary>
        /// <returns></returns>
        public Command Create()
        {
            // Custom validation of provided command input, use to validate option combinations
            //cmd.AddValidator(commandResult =>
            //{
            //    //https://github.com/dotnet/command-line-api/issues/1119
            //    if (authenticationModeOption != null)
            //    {
            //        AuthenticationMode mode = commandResult.FindResultFor(authenticationModeOption).GetValueOrDefault<AuthenticationMode>();                    

            //    }

            //    return null;
            //});

            // Binder approach as that one can handle an unlimited number of command line arguments
            var startBinder = new StartBinder(modeOption, tenantOption, environmentOption, sitesListOption, sitesFileOption,
                                              authenticationModeOption, applicationIdOption, certPathOption, certPfxFileInfoOption, certPfxFilePasswordOption);
            cmd.SetHandler(async (StartOptions arguments) =>
            {
                await HandleStartAsync(arguments);

            }, startBinder);

            return cmd;
        }

        private async Task HandleStartAsync(StartOptions arguments)
        {
            // Setup client to talk to scanner
            var client = await processManager.GetScannerClientAsync();

            // Kick off a scan
            var call = client.StartStreaming(new StartRequest
            { 
                Mode = arguments.Mode.ToString(),
                Tenant = arguments.Tenant != null ? arguments.Tenant.ToString() : "",
                Environment = arguments.Environment.ToString(),
                SitesList = arguments.SitesList !=  null ? string.Join(",", arguments.SitesList) : "",
                SitesFile = arguments.SitesFile != null ? arguments.SitesFile.FullName.ToString() : "",
                AuthMode = arguments.AuthMode.ToString(),
                ApplicationId = arguments.ApplicationId.ToString(),
            });
            await foreach (var message in call.ResponseStream.ReadAllAsync())
            {
                ColorConsole.WriteInfo($"Status: {message.Status}");
            }
        }
    }
}
