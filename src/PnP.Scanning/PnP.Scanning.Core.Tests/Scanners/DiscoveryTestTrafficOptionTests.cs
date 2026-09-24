using FluentAssertions;
using PnP.Scanning.Core.Authentication;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Process.Commands;
using System.CommandLine;
using Xunit;

namespace PnP.Scanning.Core.Tests.Scanners;

public sealed class DiscoveryTestTrafficOptionTests
{
    [Theory]
    [InlineData("", false)]
    [InlineData("--discoverytesttraffic false", false)]
    [InlineData("--discoverytesttraffic true", true)]
    [InlineData("--discoverytesttraffic", true)]
    public async Task Existing_start_command_and_binder_round_trip_the_setting(string flag, bool expected)
    {
        var handler = new StartCommandHandler(null, null, null);
        var command = handler.Create();
        // Reuse the command's real options and binder, replacing only the handler that
        // would start a process and authenticate. No network or credentials are needed.
        Option<T> Get<T>(string name) => (Option<T>)command.Options.Single(option => option.Name == name);
        var binder = new StartBinder(Get<Mode>("mode"), Get<string>("tenant"), Get<List<string>>("siteslist"),
            Get<FileInfo>("sitesfile"), Get<AuthenticationMode>("authmode"), Get<Guid>("applicationid"),
            Get<string>("tenantid"), Get<string>("certpath"), Get<FileInfo>("certfile"), Get<string>("certpassword"),
            Get<int>("threads"), new Option<bool>("syntexfull"), Get<bool>("workflowanalyze"), Get<List<ClassicComponent>>("classicinclude"),
            Get<bool>("exportwebpartproperties"), Get<bool>("skipusageinformation"), Get<bool>("skipuserinformation"),
            Get<bool>("homepageonly"), Get<bool>("discoverytesttraffic"), Get<int>("auditlogwindowdays")
#if DEBUG
            , Get<int>("testnumberofsites")
#endif
            );
        StartOptions bound = null;
        command.SetHandler((StartOptions options) => bound = options, binder);
        var args = $"--mode Classic --tenant example.com --applicationid 11111111-1111-1111-1111-111111111111 {flag}";
        command.Parse(args).Errors.Should().BeEmpty();
        (await command.InvokeAsync(args)).Should().Be(0);
        bound.DiscoveryTestTraffic.Should().Be(expected);
        var request = new StartRequest { Mode = bound.Mode.ToString() };
        ClassicStartRequestBuilder.AddClassicProperties(request, new[] { ClassicComponent.Pages },
            bound.ExportWebPartProperties, bound.SkipUsageInformation, bound.SkipUserInformation,
            bound.HomePageOnly, bound.AuditLogWindowDays, bound.DiscoveryTestTraffic);
        ((ClassicOptions)OptionsBase.FromScannerInput(request)).DiscoveryTestTraffic.Should().Be(expected);
    }

    [Theory]
    [InlineData("--mode Classic --discoverytesttraffic invalid")]
    [InlineData("--mode InfoPath --discoverytesttraffic true")]
    [InlineData("--discoverytesttraffic false --mode InfoPath")]
    public void Invalid_values_and_non_classic_modes_are_rejected(string arguments)
    {
        var command = new StartCommandHandler(null, null, null).Create();
        command.Parse(arguments + " --tenant example.com --applicationid 11111111-1111-1111-1111-111111111111")
            .Errors.Should().NotBeEmpty();
    }
}
