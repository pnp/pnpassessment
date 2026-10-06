using FluentAssertions;
using Microsoft.Extensions.Configuration;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Storage;
using System.Text;
using System.Text.Json;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery;

[Trait("Category", "PageInherits")]
public sealed class PageBaseTypeProjectionTests
{
    internal const string ConfiguredType = "  Synthetic.ConfiguredPage, Synthetic.Configuration, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null  ";
    internal const string ConfiguredBaseType = "Synthetic.ConfiguredPage, Synthetic.Configuration, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
    internal const string Provenance = "Synthetic frozen effective web.config evidence; pages.pageBaseType; fixture revision 1";
    internal static readonly PageBaseTypeFileScope Scope = new(
        "11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222",
        "33333333-3333-3333-3333-333333333333", "/sites/synthetic/default.aspx");

    internal static PageBaseTypeConfiguration ApplicableConfiguration() => new(new[]
    {
        new PageBaseTypeConfigurationEvidence("EffectiveOverride", ConfiguredType, Provenance, Scope, "SyntheticApplicableEffectiveValue"),
    });

    public static IEnumerable<object[]> ConfigurationCases()
    {
        // Hand-listed expectations, independent of the configuration serializer and evaluator.
        yield return new object[] { "Applicable", "ConfiguredDefault", "EffectiveOverride", "Applicable", ConfiguredBaseType };
        yield return new object[] { "Unknown", "FrameworkDefault", "Unknown", "Unknown", "System.Web.UI.Page" };
        yield return new object[] { "UnknownApplicable", "FrameworkDefault", "Unknown", "Applicable", "System.Web.UI.Page" };
        yield return new object[] { "NoOverride", "FrameworkDefault", "VerifiedNoOverride", "Applicable", "System.Web.UI.Page" };
        yield return new object[] { "OtherPath", "FrameworkDefault", "Unknown", "OutOfScope", "System.Web.UI.Page" };
        yield return new object[] { "OtherSite", "FrameworkDefault", "Unknown", "OutOfScope", "System.Web.UI.Page" };
        yield return new object[] { "OtherWeb", "FrameworkDefault", "Unknown", "OutOfScope", "System.Web.UI.Page" };
        yield return new object[] { "OtherFile", "FrameworkDefault", "Unknown", "OutOfScope", "System.Web.UI.Page" };
        yield return new object[] { "MissingScope", "Unknown", "Unsupported", "Unsupported", null };
        yield return new object[] { "InvalidScopeId", "Unknown", "Unsupported", "Unsupported", null };
        yield return new object[] { "EmptyScopeId", "Unknown", "Unsupported", "Unsupported", null };
        yield return new object[] { "Wildcard", "Unknown", "Unsupported", "Unsupported", null };
        yield return new object[] { "Directory", "Unknown", "Unsupported", "Unsupported", null };
        yield return new object[] { "ParentPath", "Unknown", "Unsupported", "Unsupported", null };
        yield return new object[] { "MissingProvenance", "Unknown", "Unsupported", "Applicable", null };
        yield return new object[] { "EmptyValue", "Unknown", "Unsupported", "Applicable", null };
        yield return new object[] { "NullOverride", "Unknown", "Unsupported", "Applicable", null };
        yield return new object[] { "NoOverrideWithEmpty", "Unknown", "Unsupported", "Applicable", null };
        yield return new object[] { "NoOverrideWithValue", "Unknown", "Unsupported", "Applicable", null };
        yield return new object[] { "UnknownWithValue", "Unknown", "Unsupported", "Applicable", null };
        yield return new object[] { "Unsupported", "Unknown", "Unsupported", "Applicable", null };
        yield return new object[] { "UnknownState", "Unknown", "Unsupported", "Applicable", null };
        yield return new object[] { "Conflicting", "Unknown", "Conflicting", "Applicable", null };
        yield return new object[] { "OverlappingValues", "Unknown", "Conflicting", "Applicable", null };
        yield return new object[] { "OverlappingEqualValues", "Unknown", "Conflicting", "Applicable", null };
        yield return new object[] { "OverrideAndNoOverride", "Unknown", "Conflicting", "Applicable", null };
        yield return new object[] { "BrokenFrozenJson", "Unknown", "Unsupported", "Unsupported", null };
        yield return new object[] { "UnsupportedFrozenVersion", "Unknown", "Unsupported", "Unsupported", null };
    }

    [Theory]
    [MemberData(nameof(ConfigurationCases))]
    public async Task Defaults_require_applicable_effective_value_provenance_and_exact_file_scope(
        string name, string source, string knowledge, string applicability, string baseType)
    {
        var configuration = Configuration(name);
        var read = await AspxSourceReaderTests.Capture(Encoding.UTF8.GetBytes("<%@ Page Language='C#' %>"));
        read.IsReliableSource.Should().BeTrue();
        var result = PageBaseTypeProjection.Inspect(read, configuration);
        result.Parse.Status.Should().Be(PageDirectiveParseStatus.VerifiedAbsent);
        result.Parse.IsReliableAbsence.Should().BeTrue();
        result.IsVerifiedAbsence.Should().BeTrue();
        result.DeclaredInherits.Should().BeNull();
        result.TypeSource.ToString().Should().Be(source);
        result.BaseType.Should().Be(baseType);
        result.Configuration.KnowledgeState.ToString().Should().Be(knowledge);
        result.Configuration.Applicability.ToString().Should().Be(applicability);
        result.Configuration.Reason.Should().NotBeNullOrWhiteSpace();
        result.Configuration.Evidence.Should().Equal(configuration.Evidence, "supplied/out-of-scope/error inputs remain independently inspectable");
        if (source == "FrameworkDefault")
        {
            result.IsFrameworkDefaultAssumption.Should().BeTrue();
            result.Reason.Should().Contain("FrameworkDefaultAssumption");
            result.BaseType.Should().NotBe("NotDeclared");
        }
        if (source == "ConfiguredDefault")
        {
            result.Configuration.EffectivePageBaseType.Should().Be(ConfiguredType);
            result.Configuration.Provenance.Should().Be(Provenance);
            result.Configuration.Evidence.Single().FileScope.Should().Be(Scope);
            result.Reason.Should().Contain("ApplicableFrozenPagesPageBaseType");
        }
        if (source == "Unknown") result.Reason.Should().Contain("DefaultSuppressed");
    }

    [Theory]
    [MemberData(nameof(ConfigurationCases))]
    public async Task Reliable_explicit_declaration_precedes_even_unusable_or_conflicting_configuration(
        string name, string defaultSource, string knowledge, string applicability, string defaultBaseType)
    {
        const string raw = " \tSynthetic.ExplicitPage  ";
        var read = await AspxSourceReaderTests.Capture(Encoding.UTF8.GetBytes("<%@ Page Inherits='" + raw + "' %>"));
        var result = PageBaseTypeProjection.Inspect(read, Configuration(name));
        result.TypeSource.Should().Be(PageTypeSource.Declared);
        result.TypeSource.ToString().Should().NotBe(defaultSource);
        result.DeclaredInherits.Should().Be(raw);
        result.BaseType.Should().Be("Synthetic.ExplicitPage");
        result.BaseType.Should().NotBe(defaultBaseType);
        result.NormalizedInherits.Should().Be("Synthetic.ExplicitPage");
        result.Configuration.KnowledgeState.ToString().Should().Be(knowledge);
        result.Configuration.Applicability.ToString().Should().Be(applicability);
        result.Reason.Should().Be("ExplicitPageInherits;NotRuntimeTypeProof");
        result.IsFrameworkDefaultAssumption.Should().BeFalse();
        PublishingLayoutTypeEvidence.Inspect(read.DecodedText, new()).Decision.Should().Be("Unknown", "default projection does not broaden the CLR binder");
    }

    [Fact]
    public async Task Existing_IConfiguration_supplies_a_frozen_contract_not_a_deployment_fetch()
    {
        // Literal configuration input and literal expectations, not product-generated expected JSON.
        const string json = """
            {
              "PageInherits": {
                "PagesPageBaseTypeEvidence": [
                  {
                    "KnowledgeState": "EffectiveOverride",
                    "EffectivePageBaseType": "  Synthetic.ConfiguredFromSettings  ",
                    "Provenance": "Synthetic supplied effective configuration, revision 2",
                    "Reason": "SyntheticPhysicalFileApplicabilityConfirmed",
                    "FileScope": {
                      "SiteCollectionId": "11111111-1111-1111-1111-111111111111",
                      "WebId": "22222222-2222-2222-2222-222222222222",
                      "FileUniqueId": "33333333-3333-3333-3333-333333333333",
                      "ServerRelativePath": "/sites/synthetic/default.aspx"
                    }
                  }
                ]
              }
            }
            """;
        var input = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build();
        var frozen = PageBaseTypeConfiguration.Capture(input);
        input["PageInherits:PagesPageBaseTypeEvidence:0:EffectivePageBaseType"] = "Synthetic.LaterSetting";
        var evidence = frozen.Evidence.Should().ContainSingle().Which;
        evidence.KnowledgeState.Should().Be("EffectiveOverride");
        evidence.EffectivePageBaseType.Should().Be("  Synthetic.ConfiguredFromSettings  ");
        evidence.Provenance.Should().Be("Synthetic supplied effective configuration, revision 2");
        evidence.Reason.Should().Be("SyntheticPhysicalFileApplicabilityConfirmed");
        evidence.FileScope.Should().Be(Scope);
        var restored = PageBaseTypeConfiguration.FromJson(frozen.ToJson());
        restored.Evidence.Should().Equal(frozen.Evidence);
        var result = PageBaseTypeProjection.Inspect(await AspxSourceReaderTests.Capture(Encoding.UTF8.GetBytes("<%@ Page %>")), restored);
        result.TypeSource.Should().Be(PageTypeSource.ConfiguredDefault);
        result.BaseType.Should().Be("Synthetic.ConfiguredFromSettings");
        result.Configuration.EffectivePageBaseType.Should().Be("  Synthetic.ConfiguredFromSettings  ");
        PageBaseTypeConfiguration.Capture(new ConfigurationBuilder().Build()).Evidence.Should().BeEmpty();
        PageBaseTypeConfiguration.Capture(null).Evaluate(result.SourceRead.PhysicalIdentity).KnowledgeState.Should().Be(PageConfigurationKnowledge.Unknown);
    }

    [Fact]
    public async Task Framework_default_is_an_assumption_with_unknown_configuration_not_verified_no_override()
    {
        var row = AspxSourceReaderTests.Row();
        var read = await AspxSourceReaderTests.Capture(Encoding.UTF8.GetBytes("<%@ Page Language='C#' %>"), row);
        await AspxSourceAcquisition.ForScan(new Scan { PublishingLayoutRuleVersion = 1 }, (_, _) => Task.FromResult(read))(row, default);
        var result = row.PageBaseTypeProjections.Should().ContainSingle().Which;
        result.DeclaredInherits.Should().BeNull();
        result.BaseType.Should().Be("System.Web.UI.Page");
        result.TypeSource.Should().Be(PageTypeSource.FrameworkDefault);
        result.Parse.Status.Should().Be(PageDirectiveParseStatus.VerifiedAbsent);
        result.Configuration.KnowledgeState.Should().Be(PageConfigurationKnowledge.Unknown);
        result.Configuration.Evidence.Should().BeEmpty();
        result.Reason.Should().Contain("FrameworkDefaultAssumption").And.Contain("DeploymentConfigurationUnknown");
        row.DeclaredPageType.Should().BeNull();
        row.ResolvedPageType.Should().BeNull();
        row.PublishingLayoutFamily.Should().Be("Unknown");
        row.PageTypeReason.Should().Be("InheritsMissing");
        PublishingLayoutTypeEvidence.IsConfirmedMember(row).Should().BeFalse();
        var family = JsonSerializer.Deserialize<PublishingLayoutTypeEvidence.Observation[]>(row.PageTypeEvidenceJson).Single();
        family.Declaration.Should().BeNull();
        family.Ancestry.Should().BeEmpty();
        family.ParseStatus.Should().Be("VerifiedAbsent");
        family.ParseReason.Should().Be("PageInheritsAttributeVerifiedAbsent");
        // There is no Handler observation surface in this source-only contract.
        typeof(PageBaseTypeProjection).GetProperties().Should().NotContain(value => value.Name.Contains("Handler", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Even_a_configured_family_root_cannot_populate_or_strengthen_the_family_predicate()
    {
        var row = AspxSourceReaderTests.Row();
        var configuration = new PageBaseTypeConfiguration(new[]
        {
            new PageBaseTypeConfigurationEvidence("EffectiveOverride", PublishingLayoutTypeEvidenceTests.Root, Provenance, Scope, "SyntheticRootDefault"),
        });
        var read = await AspxSourceReaderTests.Capture(Encoding.UTF8.GetBytes("<%@ Page %>"), row);
        await AspxSourceAcquisition.ForScan(new Scan { PublishingLayoutRuleVersion = 1 }, (_, _) => Task.FromResult(read), configuration)(row, default);
        row.PageBaseTypeProjections.Single().BaseType.Should().Be(PublishingLayoutTypeEvidenceTests.Root);
        row.PageBaseTypeProjections.Single().TypeSource.Should().Be(PageTypeSource.ConfiguredDefault);
        row.PublishingLayoutFamily.Should().Be("Unknown");
        row.PageTypeReason.Should().Be("InheritsMissing");
        row.DeclaredPageType.Should().BeNull();
        row.ResolvedPageType.Should().BeNull();
        PublishingLayoutTypeEvidence.IsConfirmedMember(row).Should().BeFalse();
    }

    [Theory]
    [InlineData("BareCustomPage", "", "UnresolvedIdentity")]
    [InlineData("Synthetic.CustomPage", "", "UnresolvedIdentity")]
    [InlineData(PublishingLayoutTypeEvidenceTests.Root, " CodeFile='synthetic.cs'", "DynamicCompilationUnsupported")]
    [InlineData(PublishingLayoutTypeEvidenceTests.Root, " Src='synthetic.cs'", "DynamicCompilationUnsupported")]
    public async Task Bare_names_and_dynamic_compilation_keep_declared_projection_without_family_proof(string declaration, string extra, string reason)
    {
        var row = AspxSourceReaderTests.Row();
        var read = await AspxSourceReaderTests.Capture(Encoding.UTF8.GetBytes("<%@ Page Inherits='" + declaration + "'" + extra + " %>"), row);
        await AspxSourceAcquisition.ForScan(new Scan { PublishingLayoutRuleVersion = 1 }, (_, _) => Task.FromResult(read), ApplicableConfiguration())(row, default);
        row.PageBaseTypeProjections.Single().DeclaredInherits.Should().Be(declaration);
        row.PageBaseTypeProjections.Single().BaseType.Should().Be(declaration);
        row.PageBaseTypeProjections.Single().TypeSource.Should().Be(PageTypeSource.Declared);
        row.DeclaredPageType.Should().Be(declaration);
        row.ResolvedPageType.Should().BeNull();
        row.PublishingLayoutFamily.Should().Be("Unknown");
        row.PageTypeReason.Should().Be(reason);
        PublishingLayoutTypeEvidence.IsConfirmedMember(row).Should().BeFalse();
    }

    [Fact]
    public async Task An_old_scan_does_not_gain_projection_authority_from_the_acquisition_callback()
    {
        var row = AspxSourceReaderTests.Row();
        var read = await AspxSourceReaderTests.Capture(Encoding.UTF8.GetBytes("<%@ Page %>"), row);
        await AspxSourceAcquisition.ForScan(new Scan { PublishingLayoutRuleVersion = 0 }, (_, _) => Task.FromResult(read), ApplicableConfiguration())(row, default);
        row.SourceReads.Should().ContainSingle();
        row.PageBaseTypeProjections.Should().BeEmpty();
        row.PageTypeEvidenceJson.Should().BeNull();
        row.PageTypeEvidenceOrigin.Should().Be("None");
    }

    internal static PageBaseTypeConfiguration Configuration(string name)
    {
        var entry = ApplicableConfiguration().Evidence.Single();
        PageBaseTypeConfiguration Single(PageBaseTypeConfigurationEvidence value) => new(new[] { value });
        return name switch
        {
            "Applicable" => Single(entry),
            "Unknown" => new(),
            "UnknownApplicable" => Single(entry with { KnowledgeState = "Unknown", EffectivePageBaseType = null }),
            "NoOverride" => Single(entry with { KnowledgeState = "VerifiedNoOverride", EffectivePageBaseType = null }),
            "OtherPath" => Single(entry with { FileScope = Scope with { ServerRelativePath = "/sites/synthetic/other.aspx" } }),
            "OtherSite" => Single(entry with { FileScope = Scope with { SiteCollectionId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa" } }),
            "OtherWeb" => Single(entry with { FileScope = Scope with { WebId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb" } }),
            "OtherFile" => Single(entry with { FileScope = Scope with { FileUniqueId = "cccccccc-cccc-cccc-cccc-cccccccccccc" } }),
            "MissingScope" => Single(entry with { FileScope = null }),
            "InvalidScopeId" => Single(entry with { FileScope = Scope with { SiteCollectionId = "invalid-synthetic-id" } }),
            "EmptyScopeId" => Single(entry with { FileScope = Scope with { WebId = "00000000-0000-0000-0000-000000000000" } }),
            "Wildcard" => Single(entry with { FileScope = Scope with { ServerRelativePath = "/sites/synthetic/*.aspx" } }),
            "Directory" => Single(entry with { FileScope = Scope with { ServerRelativePath = "/sites/synthetic/" } }),
            "ParentPath" => Single(entry with { FileScope = Scope with { ServerRelativePath = "/sites/synthetic/../default.aspx" } }),
            "MissingProvenance" => Single(entry with { Provenance = null }),
            "EmptyValue" => Single(entry with { EffectivePageBaseType = "" }),
            "NullOverride" => Single(entry with { EffectivePageBaseType = null }),
            "NoOverrideWithEmpty" => Single(entry with { KnowledgeState = "VerifiedNoOverride", EffectivePageBaseType = "" }),
            "NoOverrideWithValue" => Single(entry with { KnowledgeState = "VerifiedNoOverride" }),
            "UnknownWithValue" => Single(entry with { KnowledgeState = "Unknown" }),
            "Unsupported" => Single(entry with { KnowledgeState = "Unsupported", Reason = "SyntheticUnsupportedLocationInheritance" }),
            "UnknownState" => Single(entry with { KnowledgeState = "SyntheticUnrecognizedState" }),
            "Conflicting" => Single(entry with { KnowledgeState = "Conflicting", Reason = "SyntheticConflict" }),
            "OverlappingValues" => new(new[] { entry, entry with { EffectivePageBaseType = "Synthetic.OtherDefault" } }),
            "OverlappingEqualValues" => new(new[] { entry, entry }),
            "OverrideAndNoOverride" => new(new[] { entry, entry with { KnowledgeState = "VerifiedNoOverride", EffectivePageBaseType = null } }),
            "BrokenFrozenJson" => PageBaseTypeConfiguration.FromJson("{SyntheticInvalidJson"),
            "UnsupportedFrozenVersion" => PageBaseTypeConfiguration.FromJson("{\"Version\":99,\"Evidence\":[]}"),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };
    }
}
