using FluentAssertions;
using PnP.Scanning.Core.Discovery;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery;

/// <summary>Checks what the User Information List forms rule writes into the discovery evidence.</summary>
public sealed class AspxSystemListFormsPolicyTests
{
    /// <summary>Checks that the references of the rule name the rule and the platform build only.</summary>
    [Fact]
    public void RuleReferencesNameTheRuleAndThePlatformBuildOnly()
    {
        AspxSurfaceDispositionRule rule = AspxSystemListFormsPolicy.Create(
            new Dictionary<string, string> { ["baseTemplate"] = "112" },
            "/sites/a/_catalogs/users",
            "16.0.27000.12000");

        rule.Should().NotBeNull();
        rule.RuleId.Should().Be(AspxSystemListFormsPolicy.RuleId);
        rule.ReasonCode.Should().Be(AspxSystemListFormsPolicy.ReasonCode);
        rule.ReviewRef.Should().Be("sharepoint-user-information-list-forms-http-400/v1@2026-09-12");
        rule.EvidenceRefs.Should().Equal("platform-build:16.0.27000.12000");
    }

    /// <summary>Checks that a list other than the User Information List gets no rule.</summary>
    [Fact]
    public void OtherListsGetNoRule()
    {
        AspxSystemListFormsPolicy.Create(
                new Dictionary<string, string> { ["baseTemplate"] = "100" },
                "/sites/a/_catalogs/users",
                "16.0.27000.12000")
            .Should()
            .BeNull();
        AspxSystemListFormsPolicy.Create(
                new Dictionary<string, string> { ["baseTemplate"] = "112" },
                "/sites/a/Lists/Tasks",
                "16.0.27000.12000")
            .Should()
            .BeNull();
    }
}
