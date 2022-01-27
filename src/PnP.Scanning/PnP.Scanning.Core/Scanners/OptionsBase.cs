using PnP.Scanning.Core.Services;

namespace PnP.Scanning.Core.Scanners
{
    internal abstract class OptionsBase
    {
        internal static OptionsBase FromScannerInput(StartRequest request)
        {
            var options = NewOptions(request.Mode);

            // PER SCAN COMPONENT: configure scan component option handling here
#if DEBUG
            // Assign other inputs
            if (request.Mode.Equals("test", StringComparison.OrdinalIgnoreCase))
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
#if DEBUG
            if (mode.Equals("test", StringComparison.OrdinalIgnoreCase))
            {
                return new TestOptions();
            }
#endif

            throw new Exception("Unsupported scan mode passed in");
        }
    }
}
