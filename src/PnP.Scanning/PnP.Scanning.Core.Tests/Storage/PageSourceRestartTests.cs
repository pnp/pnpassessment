using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Tests.Fixtures;
using System.Text;
using Xunit;
using Scan = PnP.Scanning.Core.Storage.Scan;

namespace PnP.Scanning.Core.Tests.Storage;

[Trait("Category", "PageInherits")]
public sealed class PageSourceRestartTests
{
    [Theory]
    [InlineData("EffectiveOverride", "Synthetic.Configured", "ConfiguredDefault", "EffectiveOverride", false)]
    [InlineData("VerifiedNoOverride", null, "FrameworkDefault", "VerifiedNoOverride", true)]
    [InlineData(null, null, "FrameworkDefault", "Unknown", true)]
    public async Task Real_new_scan_initialization_and_restart_freeze_configuration_and_keep_family_Handler_and_defaults_separate(
        string knowledge, string value, string expectedTypeSource, string expectedKnowledge, bool assumption)
    {
        var settings = new Dictionary<string, string>();
        if (knowledge != null)
        {
            const string section = "PageInherits:PagesPageBaseTypeEvidence:0:";
            settings[section + "KnowledgeState"] = knowledge;
            settings[section + "EffectivePageBaseType"] = value;
            settings[section + "Provenance"] = "Synthetic, \"frozen\"\noperator assertion";
            settings[section + "Reason"] = "Synthetic frozen configuration";
            settings[section + "FileScope:SiteCollectionId"] = "11111111-1111-1111-1111-111111111111";
            settings[section + "FileScope:WebId"] = "22222222-2222-2222-2222-222222222222";
            settings[section + "FileScope:FileUniqueId"] = "33333333-3333-3333-3333-333333333333";
            settings[section + "FileScope:ServerRelativePath"] = "/sites/source/Forms/original.aspx";
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var storage = new StorageManager(new EphemeralDataProtectionProvider(), configuration);
        var scanId = Guid.NewGuid();
        await storage.LaunchNewScanAsync(scanId, new StartRequest { Mode = "Classic", AuthMode = "application", Threads = 1 }, new());
        try
        {
            Scan scan;
            string frozen;
            using (var db = new ScanContext(scanId))
            {
                scan = await db.Scans.AsNoTracking().SingleAsync();
                scan.PageSourceEvidenceVersion.Should().Be(1);
                frozen = scan.PageBaseTypeConfigurationJson;
                frozen.Should().NotBeNullOrWhiteSpace();
                PageBaseTypeConfiguration.FromJson(frozen).Evidence.Should().HaveCount(knowledge == null ? 0 : 1);
                db.SiteCollections.Add(new SiteCollection { ScanId = scanId, SiteUrl = PageSourcePersistenceFixture.Site, Status = SiteWebStatus.Running });
                db.Webs.Add(new Web
                {
                    ScanId = scanId, SiteUrl = PageSourcePersistenceFixture.Site, WebUrl = PageSourcePersistenceFixture.Web,
                    Status = SiteWebStatus.Running, Template = "STS#0",
                });
                await db.SaveChangesAsync();
            }
            using var synthetic = new PageSourcePersistenceFixture();
            var row = synthetic.Page();
            row.ScanId = scanId;
            row.DiscoveryObservation = row.DiscoveryObservation with { ScanId = scanId };
            var writer = new AssessmentDiscoveryWriter(scanId);
            await writer.WriteAsync(new[] { row });
            var read = await PageSourcePersistenceFixture.Capture(row, Encoding.UTF8.GetBytes("<%@ Page Language='C#' %>"));
            await AspxSourceAcquisition.ForAssessmentScan(scan, (_, _) => Task.FromResult(read))(row, default);
            await writer.UpdateExistingAsync(new[] { row });
            var before = (await writer.ReadPagesAsync(scanId, PageSourcePersistenceFixture.Site, PageSourcePersistenceFixture.Web)).Single();
            before.TypeSource.Should().Be(expectedTypeSource);
            before.BaseType.Should().Be(value ?? "System.Web.UI.Page");
            before.DeclaredInherits.Should().BeNull();
            before.FrameworkDefaultAssumption.Should().Be(assumption);
            before.ConfigurationKnowledgeState.Should().Be(expectedKnowledge);
            before.PublishingLayoutFamily.Should().Be("Unknown");
            before.PageTypeReason.Should().Contain("InheritsMissing");
            before.DeclaredPageType.Should().BeNull();
            before.ResolvedPageType.Should().BeNull();
            before.ObservedHandlerState.Should().Be("ServerOnlyUnavailable");
            before.ObservedHandlerReason.Should().Be("ServerDiagnosticsNotCollected;PhysicalSourceIsNotAnObservedRequestHandler");
            var retained = before.SourceEvidenceJson;
            configuration["PageInherits:PagesPageBaseTypeEvidence:0:EffectivePageBaseType"] = "Synthetic.MutableSetting";
            configuration["PageInherits:PagesPageBaseTypeEvidence:0:Provenance"] = "Synthetic changed after scan creation";
            await storage.ConsolidatedScanToEnableRestartAsync(scanId);
            await storage.RestartScanAsync(scanId);
            using (var db = new ScanContext(scanId))
            {
                scan = await db.Scans.AsNoTracking().SingleAsync();
                scan.PageSourceEvidenceVersion.Should().Be(1);
                scan.PageBaseTypeConfigurationJson.Should().Be(frozen);
                (await db.SiteCollections.SingleAsync()).Status.Should().Be(SiteWebStatus.Queued);
                (await db.Webs.SingleAsync()).Status.Should().Be(SiteWebStatus.Queued);
            }
            var after = (await writer.ReadPagesAsync(scanId, PageSourcePersistenceFixture.Site, PageSourcePersistenceFixture.Web)).Single();
            after.SourceEvidenceJson.Should().Be(retained);
            await AspxSourceAcquisition.ForAssessmentScan(scan, (_, _) => Task.FromResult(read))(after, default);
            await writer.UpdateExistingAsync(new[] { after });
            await writer.FailUnassessedPagesAsync(scanId, PageSourcePersistenceFixture.Site, PageSourcePersistenceFixture.Web,
                new IOException("Synthetic Web finalization failure"), "SyntheticFinalize");
            await writer.FinalizeScanAsync(scanId);
            await writer.FinalizeScanAsync(scanId);
            var final = (await writer.ReadPagesAsync(scanId, PageSourcePersistenceFixture.Site, PageSourcePersistenceFixture.Web)).Single();
            final.ReadSourceEvidence().Reads.Should().HaveCount(2);
            final.ReadSourceEvidence().DiscoveryObservations.Should().ContainSingle();
            final.TypeSource.Should().Be(expectedTypeSource);
            final.BaseType.Should().Be(value ?? "System.Web.UI.Page");
            final.ConfigurationKnowledgeState.Should().Be(expectedKnowledge);
            final.AssessmentStatus.Should().Be("Failed", "Web finalization cannot erase source evidence");
            final.SourceReadState.Should().Be("Complete", "a separate assessment failure cannot become a source read failure");
            final.ReadSourceEvidence().Reads[0].Page.ConfigurationProvenance.Should().Be(knowledge == null ? null : "Synthetic, \"frozen\"\noperator assertion");
            final.ReadSourceEvidence().Reads[1].Page.ConfigurationProvenance.Should().Be(knowledge == null ? null : "Synthetic, \"frozen\"\noperator assertion");
            (await writer.ReadSourceArtifactAsync(scanId, final.RecordKey, final.SourceObservationId)).Should()
                .Equal(Encoding.UTF8.GetBytes("<%@ Page Language='C#' %>"));
        }
        finally { Directory.Delete(StorageManager.GetScanDataFolder(scanId), true); }
    }

    [Fact]
    public async Task CP1_projection_authority_is_independent_of_family_rule_authority_and_never_claims_a_Handler()
    {
        using var fixture = new PageSourcePersistenceFixture();
        var scan = await fixture.SeedAsync();
        using (var db = fixture.CreateContext())
        {
            var stored = await db.Scans.SingleAsync();
            stored.PublishingLayoutRuleVersion = 0;
            await db.SaveChangesAsync();
        }
        scan.PublishingLayoutRuleVersion = 0;
        var row = fixture.Page();
        await fixture.AcquireAsync(scan, row, await PageSourcePersistenceFixture.Capture(row,
            Encoding.UTF8.GetBytes("<%@ Page Inherits='Synthetic.Independent' %>")));
        var reopened = await fixture.ReopenAsync(row.RecordKey);
        reopened.TypeSource.Should().Be("Declared");
        reopened.BaseType.Should().Be("Synthetic.Independent");
        reopened.DeclaredInherits.Should().Be("Synthetic.Independent");
        reopened.PublishingLayoutFamily.Should().Be("Unknown");
        reopened.PageTypeEvidenceOrigin.Should().Be("None");
        reopened.PageTypeEvidenceJson.Should().BeNull();
        reopened.ObservedHandlerState.Should().Be("ServerOnlyUnavailable");
        reopened.ObservedHandlerReason.Should().Contain("NotCollected").And.Contain("NotAnObservedRequestHandler");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    public async Task Inherited_or_unsupported_scan_contract_never_persists_invented_CP1_declaration_or_default_authority(int version)
    {
        using var fixture = new PageSourcePersistenceFixture();
        var scan = await fixture.SeedAsync(evidenceVersion: version);
        var row = fixture.Page();
        await fixture.AcquireAsync(scan, row, await PageSourcePersistenceFixture.Capture(row, Encoding.UTF8.GetBytes("<%@ Page %>")));
        var reopened = await fixture.ReopenAsync(row.RecordKey);
        reopened.SourceEvidenceJson.Should().BeNull();
        reopened.SourceEvidenceState.Should().Be("NotCollected");
        reopened.DeclaredInherits.Should().BeNull();
        reopened.BaseType.Should().BeNull();
        reopened.TypeSource.Should().BeNull();
        reopened.SourceRawDigest.Should().BeNull();
        reopened.SourceReadState.Should().BeNull();
        reopened.PageParseState.Should().BeNull();
        reopened.PublishingLayoutFamily.Should().Be("Unknown");
        reopened.PageTypeReason.Should().Contain("InheritsMissing", "inherited family semantics are still independent");
        row.PageBaseTypeProjections.Should().BeEmpty();
        row.SourceReads.Should().ContainSingle("the existing acquisition adapter remains compatible without CP1 persistence authority");
    }

    [Fact]
    public async Task Real_restart_of_a_handed_over_family_scan_keeps_CP1_uncollected_despite_current_configuration()
    {
        var scanId = Guid.NewGuid();
        try
        {
            using (var db = new ScanContext(scanId))
            {
                await db.GetService<IMigrator>().MigrateAsync(PageSourceUpgradeTests.IntegrationSchema);
                await db.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO Scans
                    (ScanId, StartDate, EndDate, Status, PreScanStatus, PostScanStatus, CLIThreads, CLIMode, CLIAuthMode,
                     CLITenant, CLIApplicationId, CLITenantId, CLIEnvironment, CLICertPath, CLICertFile, CLICertFilePassword,
                     PublishingLayoutRuleVersion, PublishingLayoutTypeCatalogJson)
                    VALUES ({scanId}, '2026-01-01', '2026-01-01', 0, 0, 0, 1, 'Classic', 'application', '', '', '', '', '', '', '',
                        1, {new PublishingLayoutTypeCatalog().ToJson()})");
                await db.GetService<IMigrator>().MigrateAsync();
                db.SiteCollections.Add(new SiteCollection { ScanId = scanId, SiteUrl = PageSourcePersistenceFixture.Site, Status = SiteWebStatus.Running });
                db.Webs.Add(new Web
                {
                    ScanId = scanId, SiteUrl = PageSourcePersistenceFixture.Site, WebUrl = PageSourcePersistenceFixture.Web,
                    Status = SiteWebStatus.Running, Template = "STS#0",
                });
                await db.SaveChangesAsync();
            }
            using var synthetic = new PageSourcePersistenceFixture();
            var row = synthetic.Page();
            row.ScanId = scanId;
            row.DiscoveryObservation = row.DiscoveryObservation with { ScanId = scanId };
            row.PageTypeSourceStatus = "Available";
            row.PageTypeReason = "InheritsMissing";
            row.PageTypeEvidenceOrigin = "DeclaredSource";
            row.PageTypeEvidenceJson = """
                [{"Declaration":null,"ResolvedIdentity":null,"SourceHash":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                "SourceStatus":"Available","ResolutionStatus":"Unknown","Decision":"Unknown","Reason":"InheritsMissing","Ancestry":[],"CatalogErrors":[]}]
                """;
            var writer = new AssessmentDiscoveryWriter(scanId);
            await writer.WriteAsync(new[] { row });
            var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
            {
                ["PageInherits:PagesPageBaseTypeEvidence:0:KnowledgeState"] = "EffectiveOverride",
                ["PageInherits:PagesPageBaseTypeEvidence:0:EffectivePageBaseType"] = "Synthetic.MustNotBeAdopted",
                ["PageInherits:PagesPageBaseTypeEvidence:0:Provenance"] = "Synthetic current configuration",
                ["PageInherits:PagesPageBaseTypeEvidence:0:FileScope:SiteCollectionId"] = "11111111-1111-1111-1111-111111111111",
                ["PageInherits:PagesPageBaseTypeEvidence:0:FileScope:WebId"] = "22222222-2222-2222-2222-222222222222",
                ["PageInherits:PagesPageBaseTypeEvidence:0:FileScope:FileUniqueId"] = "33333333-3333-3333-3333-333333333333",
                ["PageInherits:PagesPageBaseTypeEvidence:0:FileScope:ServerRelativePath"] = "/sites/source/Forms/original.aspx",
            }).Build();
            var storage = new StorageManager(new EphemeralDataProtectionProvider(), settings);
            await storage.ConsolidatedScanToEnableRestartAsync(scanId);
            await storage.RestartScanAsync(scanId);
            Scan inherited;
            using (var db = new ScanContext(scanId))
            {
                inherited = await db.Scans.AsNoTracking().SingleAsync();
                inherited.PublishingLayoutRuleVersion.Should().Be(1);
                inherited.PageSourceEvidenceVersion.Should().Be(0);
                inherited.PageBaseTypeConfigurationJson.Should().BeNull();
            }
            var read = await PageSourcePersistenceFixture.Capture(row, Encoding.UTF8.GetBytes("<%@ Page %>"));
            await AspxSourceAcquisition.ForAssessmentScan(inherited, (_, _) => Task.FromResult(read))(row, default);
            await writer.UpdateExistingAsync(new[] { row });
            await writer.FinalizeScanAsync(scanId);
            var retained = (await writer.ReadPagesAsync(scanId, PageSourcePersistenceFixture.Site, PageSourcePersistenceFixture.Web)).Single();
            retained.SourceEvidenceJson.Should().BeNull();
            retained.SourceEvidenceState.Should().Be("NotCollected");
            retained.TypeSource.Should().BeNull();
            retained.BaseType.Should().BeNull();
            retained.SourceRawDigest.Should().BeNull();
            retained.SourceCapturedByteLength.Should().BeNull();
            retained.SourceReadState.Should().BeNull();
            retained.PageParseState.Should().BeNull();
            retained.PageTypeEvidenceJson.Should().Contain("\"SourceHash\":\"AAAAAAAA");
            retained.PageTypeReason.Should().Contain("InheritsMissing");
            row.PageBaseTypeProjections.Should().BeEmpty();
        }
        finally { Directory.Delete(StorageManager.GetScanDataFolder(scanId), true); }
    }
}
