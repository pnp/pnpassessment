using System.CommandLine;
using System.CommandLine.Binding;

namespace PnP.Scanning.Process.Commands
{
    internal sealed class StartBinder: BinderBase<StartOptions>
    {
        private readonly Option<Mode> mode;
        private readonly Option<string> tenant;
        private readonly Option<Microsoft365Environment> environment;
        private readonly Option<List<string>> sitesList;
        private readonly Option<FileInfo> sitesFile;
        private readonly Option<AuthenticationMode> authMode;
        private readonly Option<Guid> applicationId;
        private readonly Option<string> certPath;
        private readonly Option<FileInfo> certFile;
        private readonly Option<string> certPassword;

        public StartBinder(Option<Mode> modeInput, Option<string> tenantInput, Option<Microsoft365Environment> environmentInput, Option<List<string>> sitesListInput, Option<FileInfo> sitesFileInput,
                           Option<AuthenticationMode> authModeInput, Option<Guid> applicationIdInput, Option<string> certPathInput, Option<FileInfo> certFileInput, Option<string> certPasswordInput)
        {
            mode = modeInput;
            tenant = tenantInput;
            environment = environmentInput;
            sitesList = sitesListInput; 
            sitesFile = sitesFileInput;
            authMode = authModeInput;
            applicationId = applicationIdInput;
            certPath = certPathInput;
            certFile = certFileInput;
            certPassword = certPasswordInput;
        }

        protected override StartOptions GetBoundValue(BindingContext bindingContext) =>
            new()
            {
                Mode = bindingContext.ParseResult.GetValueForOption(mode),
                Tenant = bindingContext.ParseResult.GetValueForOption(tenant),
                Environment = bindingContext.ParseResult.GetValueForOption(environment),
                SitesList = bindingContext.ParseResult.GetValueForOption(sitesList),
                SitesFile = bindingContext.ParseResult.GetValueForOption(sitesFile),
                AuthMode = bindingContext.ParseResult.GetValueForOption(authMode),
                ApplicationId = bindingContext.ParseResult.GetValueForOption(applicationId),
                CertPath = bindingContext.ParseResult.GetValueForOption(certPath),
                CertFile = bindingContext.ParseResult.GetValueForOption(certFile),
                CertPassword = bindingContext.ParseResult.GetValueForOption(certPassword),
            };
    }
}
