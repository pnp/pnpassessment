using FluentAssertions;
using System.Runtime.InteropServices;
using Xunit;
using Xunit.Abstractions;

namespace PnP.Scanning.Core.Tests.Discovery;

[Trait("Category", "PageInherits")]
public sealed class AspxSourceRuntimeTests
{
    private readonly ITestOutputHelper output;
    public AspxSourceRuntimeTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public void Net8_test_host_uses_matching_core_and_aspnet_shared_frameworks()
    {
        Environment.Version.Major.Should().Be(8, "net8.0 fixtures must not depend on major runtime roll-forward");
        var core = typeof(object).Assembly;
        var aspnet = typeof(Microsoft.AspNetCore.Http.DefaultHttpContext).Assembly;
        core.GetName().Version.Major.Should().Be(8);
        aspnet.GetName().Version.Major.Should().Be(8);
        output.WriteLine("FrameworkDescription=" + RuntimeInformation.FrameworkDescription);
        output.WriteLine("ProcessArchitecture=" + RuntimeInformation.ProcessArchitecture);
        output.WriteLine("CoreSharedFrameworkAssembly=" + core.Location);
        output.WriteLine("AspNetSharedFrameworkAssembly=" + aspnet.Location);
        output.WriteLine("ConfiguredRuntimeRoot=" + Environment.GetEnvironmentVariable("DOTNET_ROOT"));
        output.WriteLine("MajorRollForward=" + Environment.GetEnvironmentVariable("DOTNET_ROLL_FORWARD"));
        output.WriteLine("ObservedAtUtc=" + DateTimeOffset.UtcNow.ToString("o"));
    }
}
