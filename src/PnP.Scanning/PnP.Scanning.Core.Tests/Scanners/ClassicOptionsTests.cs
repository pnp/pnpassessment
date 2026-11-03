using FluentAssertions;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Services;
using Xunit;

namespace PnP.Scanning.Core.Tests.Scanners
{
    /// <summary>
    /// T2 — verifies the new Classic page-scan option flags plumb through cleanly:
    /// gRPC <see cref="StartRequest"/> properties → <see cref="OptionsBase.FromScannerInput"/> →
    /// <see cref="ClassicOptions"/>, and that the CLI write-side
    /// (<see cref="ClassicStartRequestBuilder"/>) emits exactly the keys the read-side consumes.
    /// </summary>
    public class ClassicOptionsTests
    {
        // (property key, flag name) — the flag name selects which ClassicOptions property to read.
        // A string identifier is used (rather than a Func<ClassicOptions, bool>) because
        // ClassicOptions is internal and cannot appear in a public xUnit theory signature.
        public static IEnumerable<object[]> FlagCases()
        {
            yield return new object[] { Constants.StartClassicExportWebPartProperties, nameof(ClassicOptions.ExportWebPartProperties) };
            yield return new object[] { Constants.StartClassicSkipUsageInformation, nameof(ClassicOptions.SkipUsageInformation) };
            yield return new object[] { Constants.StartClassicSkipUserInformation, nameof(ClassicOptions.SkipUserInformation) };
            yield return new object[] { Constants.StartClassicHomePageOnly, nameof(ClassicOptions.HomePageOnly) };
        }

        [Theory]
        [MemberData(nameof(FlagCases))]
        public void Options_FromScannerInput_MapsFlags(string propertyKey, string flagName)
        {
            // Property present and "True" → flag set.
            var whenTrue = (ClassicOptions)OptionsBase.FromScannerInput(ClassicRequest((propertyKey, bool.TrueString)));
            ReadFlag(whenTrue, flagName).Should().BeTrue();

            // Property present and "False" → flag explicitly cleared.
            var whenFalse = (ClassicOptions)OptionsBase.FromScannerInput(ClassicRequest((propertyKey, bool.FalseString)));
            ReadFlag(whenFalse, flagName).Should().BeFalse();
        }

        private static bool ReadFlag(ClassicOptions options, string flagName) => flagName switch
        {
            nameof(ClassicOptions.ExportWebPartProperties) => options.ExportWebPartProperties,
            nameof(ClassicOptions.SkipUsageInformation) => options.SkipUsageInformation,
            nameof(ClassicOptions.SkipUserInformation) => options.SkipUserInformation,
            nameof(ClassicOptions.HomePageOnly) => options.HomePageOnly,
            _ => throw new ArgumentOutOfRangeException(nameof(flagName), flagName, "Unknown flag"),
        };

        [Fact]
        public void Options_FromScannerInput_FlagsDefaultFalse_WhenAbsent()
        {
            // No page-scan flag properties at all → every flag defaults to false.
            var options = (ClassicOptions)OptionsBase.FromScannerInput(ClassicRequest());

            options.ExportWebPartProperties.Should().BeFalse();
            options.SkipUsageInformation.Should().BeFalse();
            options.SkipUserInformation.Should().BeFalse();
            options.HomePageOnly.Should().BeFalse();
        }

        [Fact]
        public void Options_RoundTrip_CliToProperties()
        {
            // Build the gRPC request exactly as the CLI handler does, then read it back: this
            // proves the keys the write-side emits match the keys FromScannerInput consumes.
            var request = new StartRequest { Mode = Mode.Classic.ToString() };

            ClassicStartRequestBuilder.AddClassicProperties(
                request,
                new[] { ClassicComponent.Pages },
                exportWebPartProperties: true,
                skipUsageInformation: true,
                skipUserInformation: false,
                homePageOnly: true);

            var options = (ClassicOptions)OptionsBase.FromScannerInput(request);

            // Scan component still round-trips...
            options.Pages.Should().BeTrue();

            // ...and the new flags arrive with the values the CLI supplied.
            options.ExportWebPartProperties.Should().BeTrue();
            options.SkipUsageInformation.Should().BeTrue();
            options.SkipUserInformation.Should().BeFalse();
            options.HomePageOnly.Should().BeTrue();
            options.AuditLogWindowDays.Should().Be(14); // default when not explicitly passed
        }

        private static StartRequest ClassicRequest(params (string Key, string Value)[] properties)
        {
            var request = new StartRequest { Mode = Mode.Classic.ToString() };
            foreach (var (key, value) in properties)
            {
                request.Properties.Add(new PropertyRequest
                {
                    Property = key,
                    Type = "bool",
                    Value = value,
                });
            }

            return request;
        }
    }
}
