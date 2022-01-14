using PnP.Scanning.Core.Services;

namespace PnP.Scanning.Core.Scanners
{
    internal abstract class OptionsBase
    {
        internal static OptionsBase FromGrpcInput(StartRequest request)
        {
            var options = NewOptions(request.Mode);

            // Assign other inputs
            // ...

            return options;
        }

        private static OptionsBase NewOptions(string mode)
        {
            if (mode.Equals("test", StringComparison.OrdinalIgnoreCase))
            {
                return new TestOptions();
            }

            throw new Exception("Unsupported scan mode passed in");
        }
    }
}
