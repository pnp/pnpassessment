using CsvHelper.Configuration.Attributes;
using FluentAssertions;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Tests.Fixtures;
using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery;

[Trait("Category", "PageInherits")]
public sealed class PageBaseTypeBoundaryTests : IClassFixture<ScanContextFixture>
{
    private readonly ScanContextFixture database;
    public PageBaseTypeBoundaryTests(ScanContextFixture database) => this.database = database;

    [Fact]
    public void Projection_is_an_explicit_transient_consumer_boundary_not_an_unmigrated_schema_or_csv_change()
    {
        using var db = database.CreateContext();
        var entity = db.Model.FindEntityType(typeof(ClassicPageDiscovery));
        entity.FindProperty(nameof(ClassicPageDiscovery.PageBaseTypeProjections)).Should().BeNull();
        entity.FindNavigation(nameof(ClassicPageDiscovery.PageBaseTypeProjections)).Should().BeNull();
        db.Model.GetEntityTypes().Should().NotContain(value => value.ClrType == typeof(PageBaseTypeProjection) ||
            value.ClrType == typeof(PageDirectiveParseResult) || value.ClrType == typeof(PageBaseTypeConfiguration));
        var property = typeof(ClassicPageDiscovery).GetProperty(nameof(ClassicPageDiscovery.PageBaseTypeProjections));
        property.GetCustomAttribute<NotMappedAttribute>().Should().NotBeNull();
        property.GetCustomAttribute<IgnoreAttribute>().Should().NotBeNull();
    }

    [Fact]
    public async Task Legacy_and_read_result_family_wrappers_use_the_same_directive_evidence_without_default_binding()
    {
        var row = AspxSourceReaderTests.Row();
        var page = PublishingLayoutTypeEvidenceTests.Source(PublishingLayoutTypeEvidenceTests.Root);
        var text = "<!-- synthetic example: <%@ Page Inherits='Synthetic.Fake' %> -->" + page +
            "<div data-example=\"<%@ Page Inherits='Synthetic.Fake' %>\">body</div>";
        var read = await AspxSourceReaderTests.Capture(Encoding.UTF8.GetBytes(text), row);
        var old = PublishingLayoutTypeEvidence.Inspect(text, new());
        await AspxSourceAcquisition.ForScan(new Scan { PublishingLayoutRuleVersion = 1 },
            (_, _) => Task.FromResult(read), PageBaseTypeProjectionTests.ApplicableConfiguration())(row, default);
        var projected = row.PageBaseTypeProjections.Single();
        var family = JsonSerializer.Deserialize<PublishingLayoutTypeEvidence.Observation[]>(row.PageTypeEvidenceJson).Single();
        projected.TypeSource.Should().Be(PageTypeSource.Declared);
        projected.SourceRead.Should().BeSameAs(read);
        old.Decision.Should().Be("Member");
        family.Should().BeEquivalentTo(old);
        family.DirectiveEvidence.Should().Be(page);
        projected.Parse.RawPageDirectiveEvidence.Should().Be(page);
        family.Declaration.Should().Be(projected.DeclaredInherits);
        PublishingLayoutTypeEvidence.IsConfirmedMember(row).Should().BeTrue("a valid explicit qualified root remains a positive family control");
    }

    [Fact]
    public void Old_family_observation_json_is_read_without_inventing_new_parse_or_default_evidence()
    {
        const string json = """
            [{
              "Declaration": null, "ResolvedIdentity": null, "SourceHash": null,
              "SourceStatus": "Unknown", "ResolutionStatus": "Unknown",
              "Decision": "Unknown", "Reason": "SourceUnavailable",
              "Ancestry": [], "CatalogErrors": []
            }]
            """;
        var old = JsonSerializer.Deserialize<PublishingLayoutTypeEvidence.Observation[]>(json).Single();
        old.ParseStatus.Should().BeNull();
        old.ParseReason.Should().BeNull();
        old.DirectiveEvidence.Should().BeNull();
        old.Declaration.Should().BeNull();
        old.ResolvedIdentity.Should().BeNull();
        old.Decision.Should().Be("Unknown");
        old.Reason.Should().Be("SourceUnavailable");
    }

    [Fact]
    public async Task Cancellation_creates_no_projection_or_successful_default_observation()
    {
        var row = AspxSourceReaderTests.Row();
        var callback = AspxSourceAcquisition.ForScan(new Scan { PublishingLayoutRuleVersion = 1 },
            (_, _) => throw new OperationCanceledException("Synthetic source cancellation"),
            PageBaseTypeProjectionTests.ApplicableConfiguration());
        var operation = () => callback(row, default);
        await operation.Should().ThrowAsync<OperationCanceledException>();
        row.SourceReads.Should().BeEmpty();
        row.PageBaseTypeProjections.Should().BeEmpty();
        row.PageTypeEvidenceJson.Should().BeNull();
    }

    [Fact]
    public async Task Configuration_limits_are_unsupported_not_verified_no_override()
    {
        var configuration = new PageBaseTypeConfiguration(Enumerable.Repeat(PageBaseTypeProjectionTests.ApplicableConfiguration().Evidence.Single(),
            PageBaseTypeConfiguration.MaximumEvidenceEntries + 1));
        var result = PageBaseTypeProjection.Inspect(await AspxSourceReaderTests.Capture(Encoding.UTF8.GetBytes("<%@ Page %>")), configuration);
        result.Configuration.KnowledgeState.Should().Be(PageConfigurationKnowledge.Unsupported);
        result.Configuration.Reason.Should().Contain("ConfigurationEvidenceCountLimitExceeded");
        result.TypeSource.Should().Be(PageTypeSource.Unknown);
        result.BaseType.Should().BeNull();
        var largeFrozen = PageBaseTypeConfiguration.FromJson(new string(' ', 1048577));
        largeFrozen.Evaluate(result.SourceRead.PhysicalIdentity).Reason.Should().Contain("FrozenConfigurationCharacterLimitExceeded");
    }
}
