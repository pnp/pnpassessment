using FluentAssertions;
using PnP.Scanning.Core.Discovery.M1;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery.M1;

public class SiteTemplateExclusionPolicyTests
{
    /// <summary>
    /// Applies personal-site and app-web defaults only when the exclusion option is absent.
    /// </summary>
    [Theory]
    [InlineData("SPSPERS#0", true)]
    [InlineData("SPSPERS#10", true)]
    [InlineData("app#0", true)]
    [InlineData("STS#3", false)]
    [InlineData("CMSPUBLISHING#0", false)]
    public void MissingOptionUsesPersonalAndAppDefaults(string template, bool excluded) =>
        SiteTemplateExclusionPolicy.Parse(null).IsExcluded(template).Should().Be(excluded);

    /// <summary>
    /// Treats an explicit exclusion list as the complete policy rather than appending built-in defaults.
    /// </summary>
    [Fact]
    public void ExplicitListReplacesRatherThanExtendsDefaults()
    {
        var policy = SiteTemplateExclusionPolicy.Parse("STS#3");
        policy.IsExcluded("STS#3").Should().BeTrue();
        policy.IsExcluded("SPSPERS#0").Should().BeFalse();
        policy.IsExcluded("APP#0").Should().BeFalse();
    }

    /// <summary>
    /// Accepts a sole case-insensitive none token as an explicit empty policy and canonicalizes its stored value.
    /// </summary>
    [Fact]
    public void SoleNoneClearsEveryDefault()
    {
        var policy = SiteTemplateExclusionPolicy.Parse(" NoNe ");
        policy.Patterns.Should().BeEmpty();
        policy.CanonicalValue.Should().Be("none");
        policy.IsExcluded("SPSPERS#0").Should().BeFalse();
        policy.IsExcluded("APP#0").Should().BeFalse();
    }

    /// <summary>
    /// Rejects empty, mixed-none, malformed, and URL-shaped explicit policies instead of reverting silently to defaults.
    /// </summary>
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

    /// <summary>
    /// Normalizes, orders, and deduplicates patterns while preserving the policy through canonical-value parsing.
    /// </summary>
    [Fact]
    public void CanonicalValueRoundTripsAndDeduplicates()
    {
        var policy = SiteTemplateExclusionPolicy.Parse(" STS#3,app#*,sts#3 ");
        SiteTemplateExclusionPolicy.Parse(policy.CanonicalValue).Patterns.Should().Equal(policy.Patterns);
        policy.Patterns.Should().Equal("APP#*", "STS#3");
    }

    /// <summary>
    /// Reports a missing template as unevaluable without asserting that the web is excluded.
    /// </summary>
    [Fact]
    public void MissingTemplateIsUnknownRatherThanExcluded()
    {
        SiteTemplateExclusionPolicy.Parse(null).TryEvaluate(null, out var excluded).Should().BeFalse();
        excluded.Should().BeFalse();
    }

    /// <summary>
    /// Evaluates each template independently so excluding one parent template does not exclude a different child template.
    /// </summary>
    [Fact]
    public void ExcludedParentDoesNotExcludeDifferentChildTemplate()
    {
        var policy = SiteTemplateExclusionPolicy.Parse("STS#3");
        policy.IsExcluded("STS#3").Should().BeTrue();
        policy.IsExcluded("CMSPUBLISHING#0").Should().BeFalse();
    }
}
