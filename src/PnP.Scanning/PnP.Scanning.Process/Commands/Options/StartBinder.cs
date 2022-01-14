using System.CommandLine;
using System.CommandLine.Binding;

namespace PnP.Scanning.Process.Commands
{
    internal sealed class StartBinder: BinderBase<StartOptions>
    {
        private readonly Option<Mode> mode;
        private readonly Option<AuthenticationMode> authMode;
        private readonly Option<string> certPath;
        private readonly Option<FileInfo> certFile;

        public StartBinder(Option<Mode> modeInput, Option<AuthenticationMode> authModeInput, Option<string> certPathInput, Option<FileInfo> certFileInput)
        {
            mode = modeInput;
            authMode = authModeInput;
            certPath = certPathInput;
            certFile = certFileInput;
        }

        protected override StartOptions GetBoundValue(BindingContext bindingContext) =>
            new()
            {
                Mode = bindingContext.ParseResult.GetValueForOption(mode),
                AuthMode = bindingContext.ParseResult.GetValueForOption(authMode),
                CertPath = bindingContext.ParseResult.GetValueForOption(certPath),
                CertFile = bindingContext.ParseResult.GetValueForOption(certFile),
            };
    }
}
