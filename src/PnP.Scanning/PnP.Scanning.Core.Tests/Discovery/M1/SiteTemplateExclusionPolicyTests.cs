using FluentAssertions;
using PnP.Scanning.Core.Discovery.M1;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery.M1;

public class SiteTemplateExclusionPolicyTests
{
    [Theory]
    [InlineData("SPSPERS#0", true)]
    [InlineData("SPSPERS#10", true)]
    [InlineData("app#0", true)]
    [InlineData("STS#3", false)]
    [InlineData("CMSPUBLISHING#0", false)]
    public void MissingOptionUsesPersonalAndAppDefaults(string template, bool excluded) =>
        SiteTemplateExclusionPolicy.Parse(null).IsExcluded(template).Should().Be(excluded);

    [Fact]
    public void ExplicitListReplacesRatherThanExtendsDefaults()
    {
        var policy = SiteTemplateExclusionPolicy.Parse("STS#3");
        policy.IsExcluded("STS#3").Should().BeTrue();
        policy.IsExcluded("SPSPERS#0").Should().BeFalse();
        policy.IsExcluded("APP#0").Should().BeFalse();
    }

    [Fact]
    public void SoleNoneClearsEveryDefault()
    {
        var policy = SiteTemplateExclusionPolicy.Parse(" NoNe ");
        policy.Patterns.Should().BeEmpty();
        policy.CanonicalValue.Should().Be("none");
        policy.IsExcluded("SPSPERS#0").Should().BeFalse();
        policy.IsExcluded("APP#0").Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("none,APP#*")]
    [InlineData("APP#*,")]
    [InlineData("APP#*,,STS#3")]
    [InlineData("https://example.test")]
    public void InvalidExplicitScopeDoesNotSilentlyBecomeDefault(string value)
    {
        Action parse = () => SiteTemplateExclusionPolicy.Parse(value);
        parse.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CanonicalValueRoundTripsAndDeduplicates()
    {
        var policy = SiteTemplateExclusionPolicy.Parse(" STS#3,app#*,sts#3 ");
        SiteTemplateExclusionPolicy.Parse(policy.CanonicalValue).Patterns.Should().Equal(policy.Patterns);
        policy.Patterns.Should().Equal("APP#*", "STS#3");
    }

    [Fact]
    public void MissingTemplateIsUnknownRatherThanExcluded()
    {
        SiteTemplateExclusionPolicy.Parse(null).TryEvaluate(null, out var excluded).Should().BeFalse();
        excluded.Should().BeFalse();
    }

    [Fact]
    public void ExcludedParentDoesNotExcludeDifferentChildTemplate()
    {
        var policy = SiteTemplateExclusionPolicy.Parse("STS#3");
        policy.IsExcluded("STS#3").Should().BeTrue();
        policy.IsExcluded("CMSPUBLISHING#0").Should().BeFalse();
    }
}
