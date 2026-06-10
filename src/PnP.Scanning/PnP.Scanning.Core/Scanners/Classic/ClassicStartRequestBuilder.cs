using PnP.Scanning.Core.Services;

namespace PnP.Scanning.Core.Scanners
{
    /// <summary>
    /// Builds the gRPC <see cref="StartRequest"/> properties for a Classic assessment from the CLI
    /// option values. This is the write-side counterpart of
    /// <see cref="OptionsBase.FromScannerInput(StartRequest)"/>: both sides agree on the property
    /// keys (the <see cref="ClassicComponent"/> enum names plus the Classic flag constants) so the
    /// CLI options round-trip cleanly into <see cref="ClassicOptions"/>. Keeping this as a pure,
    /// dependency-free method lets the CLI → properties → options round-trip be unit-tested without
    /// the gRPC host.
    /// </summary>
    internal static class ClassicStartRequestBuilder
    {
        internal static void AddClassicProperties(
            StartRequest request,
            IEnumerable<ClassicComponent> components,
            bool exportWebPartProperties,
            bool skipUsageInformation,
            bool skipUserInformation,
            bool homePageOnly)
        {
            // Each requested scan component is keyed by its enum name and is always "on" (true);
            // FromScannerInput reads these as the set of enabled ClassicComponents.
            foreach (var classicComponent in components)
            {
                request.Properties.Add(new PropertyRequest
                {
                    Property = $"{classicComponent}",
                    Type = "bool",
                    Value = true.ToString(),
                });
            }

            AddBool(request, Constants.StartClassicExportWebPartProperties, exportWebPartProperties);
            AddBool(request, Constants.StartClassicSkipUsageInformation, skipUsageInformation);
            AddBool(request, Constants.StartClassicSkipUserInformation, skipUserInformation);
            AddBool(request, Constants.StartClassicHomePageOnly, homePageOnly);
        }

        private static void AddBool(StartRequest request, string property, bool value)
        {
            request.Properties.Add(new PropertyRequest
            {
                Property = property,
                Type = "bool",
                Value = value.ToString(),
            });
        }
    }
}
