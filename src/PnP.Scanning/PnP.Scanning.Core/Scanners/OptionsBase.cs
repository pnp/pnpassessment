using PnP.Scanning.Core.Services;

namespace PnP.Scanning.Core.Scanners
{
    internal abstract class OptionsBase
    {
        internal string? Mode { get; private set; }

        internal static OptionsBase FromScannerInput(StartRequest request)
        {
            var options = NewOptions(request.Mode);

            // PER SCAN COMPONENT: configure scan component option handling here
            if (request.Mode.Equals(Services.Mode.Syntex.ToString(), StringComparison.OrdinalIgnoreCase))
            { 
            
            }
#if DEBUG
            // Assign other inputs
            else if (request.Mode.Equals(Services.Mode.Test.ToString(), StringComparison.OrdinalIgnoreCase))
            {
#pragma warning disable CS8602 // Dereference of a possibly null reference.
                (options as TestOptions).TestNumberOfSites = int.Parse(request.Properties.First().Value);
#pragma warning restore CS8602 // Dereference of a possibly null reference.
            }
#endif

            return options;
        }

        private static OptionsBase NewOptions(string mode)
        {
            // PER SCAN COMPONENT: configure scan component option handling here
            if (mode.Equals(Services.Mode.Syntex.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return new SyntexOptions
                {
                    Mode = mode,
                };
            }
#if DEBUG
            else if (mode.Equals(Services.Mode.Test.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return new TestOptions
                {
                    Mode = mode,
                };
            }
#endif

            throw new Exception("Unsupported scan mode passed in");
        }
    }
}
