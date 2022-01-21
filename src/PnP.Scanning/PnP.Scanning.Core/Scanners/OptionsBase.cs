using PnP.Scanning.Core.Services;

namespace PnP.Scanning.Core.Scanners
{
    internal abstract class OptionsBase
    {
        internal static OptionsBase FromScannerInput(StartRequest request)
        {
            var options = NewOptions(request.Mode);

#if DEBUG
            // Assign other inputs
            if (request.Mode.Equals("test", StringComparison.OrdinalIgnoreCase))
            {
                (options as TestOptions).TestNumberOfSites = int.Parse(request.Properties.First().Value);
            }
#endif

            return options;
        }

        private static OptionsBase NewOptions(string mode)
        {
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
