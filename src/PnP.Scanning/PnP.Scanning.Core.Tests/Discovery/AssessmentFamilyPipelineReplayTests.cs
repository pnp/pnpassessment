using CsvHelper.Configuration;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.SharePoint.Client;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Xunit;
using TypeFixture = PnP.Scanning.Core.Tests.Discovery.PublishingLayoutTypeEvidenceTests;

namespace PnP.Scanning.Core.Tests.Discovery;

public sealed partial class AssessmentPageMetadataReplayTests
{
    [Theory]
    [InlineData("Root", "Resolved", "ConfirmedPublishingLayoutFamily")]
    [InlineData("Direct", "Resolved", "ConfirmedPublishingLayoutFamily")]
    [InlineData("Indirect", "Resolved", "ConfirmedPublishingLayoutFamily")]
    [InlineData("Outside", "Unresolved", "TargetNotPublishingLayoutFamily")]
    [InlineData("PublishingLayoutPageLookalike", "Unresolved", "TargetNotPublishingLayoutFamily")]
    [InlineData("Unavailable", "Unresolved", "TargetTypeSourceUnknown")]
    [InlineData("Denied", "Unresolved", "TargetTypeSourceDenied")]
    [InlineData("Failed", "Unresolved", "TargetTypeSourceFailed")]
    [InlineData("Incomplete", "Unresolved", "TargetTypeFamilyUnknown")]
    [InlineData("Missing", "Unresolved", "TargetTypeFamilyUnknown")]
    [InlineData("Undiscovered", "Unresolved", "TargetNotDiscovered")]
    [InlineData("OtherScan", "Unresolved", "TargetNotDiscovered")]
    [InlineData("OtherSite", "Unresolved", "TargetNotDiscovered")]
    [InlineData("OtherSiteId", "Unresolved", "TargetNotDiscovered")]
    [InlineData("NoMetadata", "Unresolved", "ReferenceMetadataMissing")]
    public async Task Raw_inventory_and_source_acquisition_drive_family_accounting_and_references(
        string selection, string expectedStatus, string expectedReason)
    {
        var scan = Guid.NewGuid();
        using var assembly = new TypeFixture.MetadataFixture();
        var authority = new Core.Storage.Scan
        {
            ScanId = scan, PublishingLayoutRuleVersion = 1,
            PublishingLayoutTypeCatalogJson = PublishingLayoutTypeCatalog.Capture(new[] { assembly.Path }).ToJson(),
        };
        using (var db = database.CreateContext())
        {
            db.Scans.Add(authority);
            await db.SaveChangesAsync();
        }
        var members = new[] { "Root", "Direct", "Indirect" };
        var nonmembers = new[] { "Outside", "PublishingLayoutPageLookalike" };
        var names = members.Concat(nonmembers).Concat(new[] { "Unavailable", "Denied", "Failed", "Incomplete", "Missing" }).ToArray();
        var fixtures = names.Select(name => new MetadataFixture(scan,
            (name == "Root" ? "_catalogs/masterpage/" : "Custom/") + name + ".aspx", new()
            {
                ["ContentTypeId"] = name == "Indirect" ? PublishingContentType : name == "Direct" ? null :
                    AspxAssetPurpose.LayoutContentType.ToLowerInvariant() + (name == "Outside" ? "" : "00aabb"),
                ["WikiField"] = "<p>Own body: " + name + "</p>",
            })).ToList();
        foreach (var fixture in fixtures)
        {
            string name = Path.GetFileNameWithoutExtension(fixture.Row.FileName);
            fixture.Row.ContentTypeId = name == "Direct" ? null : name == "Indirect" ? PublishingContentType :
                AspxAssetPurpose.LayoutContentType.ToLowerInvariant() + (name == "Outside" ? "" : "00aabb");
            fixture.Row.PageType = "PublishingPage"; // A stale projection must not confer type authority.
            if (name == "Root") { fixture.Row.ListId = null; fixture.Row.ListItemId = null; }
        }
        string selectedUrl = fixtures.SingleOrDefault(f => f.Row.FileName == selection + ".aspx")?.Row.Url ?? Web + "/Custom/" + selection + ".aspx";
        var fields = new Dictionary<string, object>();
        if (selection != "NoMetadata") fields[PublishingLayoutReference.FieldName] = new FieldUrlValue { Url = selectedUrl, Description = "ArticleLeft" };
        var envelope = new MetadataFixture(scan, "subweb/Pages/article.aspx", new() { ["ContentTypeId"] = PublishingContentType });
        envelope.Row.ContentTypeId = PublishingContentType;
        envelope.Row.WebUrl += "/subweb";
        envelope.Row.WebId = Guid.NewGuid();
        fixtures.Insert(0, envelope); // The outer page's web is discovered first.
        if (selection is "OtherScan" or "OtherSite" or "OtherSiteId")
        {
            var foreign = new MetadataFixture(selection == "OtherScan" ? Guid.NewGuid() : scan, "Custom/" + selection + ".aspx", new());
            foreign.Row.WebId = Guid.NewGuid();
            if (selection == "OtherSite") foreign.Row.SiteUrl = Site + "-other";
            if (selection is "OtherSite" or "OtherSiteId") foreign.Row.SiteCollectionId = Guid.NewGuid();
            foreign.Row.ListId = null;
            foreign.Row.ListItemId = null;
            fixtures.Add(foreign);
            if (selection == "OtherScan")
            {
                using var db = database.CreateContext();
                db.Scans.Add(new Core.Storage.Scan { ScanId = foreign.Row.ScanId, PublishingLayoutRuleVersion = 1,
                    PublishingLayoutTypeCatalogJson = authority.PublishingLayoutTypeCatalogJson });
                await db.SaveChangesAsync();
            }
        }
        var writer = new AssessmentDiscoveryWriter(database.CreateContext);
        var acquired = new List<Guid?>();
        foreach (var group in fixtures.GroupBy(f => (f.Row.ScanId, f.Row.SiteUrl, f.Row.WebUrl)))
        {
            using var provider = new PipelineProvider(group.Select(f => f.Row).ToArray());
            var inspect = PublishingLayoutTypeEvidence.ForScan(authority, async (row, _) =>
            {
                (await writer.ReadPagesAsync(row.ScanId, row.SiteUrl, row.WebUrl)).Should().Contain(value => value.FileUniqueId == row.FileUniqueId,
                    "physical existence must be committed before source acquisition");
                acquired.Add(row.FileUniqueId);
                var name = Path.GetFileNameWithoutExtension(row.FileName);
                return name switch
                {
                    "Denied" => throw new UnauthorizedAccessException("Synthetic source denial"),
                    "Failed" => throw new IOException("Synthetic source failure"),
                    "Unavailable" => null,
                    "Root" or "OtherScan" or "OtherSite" or "OtherSiteId" => TypeFixture.Source(TypeFixture.Root),
                    "article" => TypeFixture.Source(TypeFixture.Root.Replace("PublishingLayoutPage", "TemplateRedirectionPage")) +
                        "<script runat='server'>HttpContext.Current.Handler = new PublishingLayoutPage();</script>",
                    _ => TypeFixture.Source(TypeFixture.Identity(name)),
                };
            });
            await new AssessmentWebDiscovery(group.Key.ScanId, group.Key.SiteUrl, group.Key.WebUrl,
                writer, inspect, authority.PublishingLayoutRuleVersion).RunAsync(provider, default);
            var discovered = await writer.ReadPagesAsync(group.Key.ScanId, group.Key.SiteUrl, group.Key.WebUrl);
            foreach (var fixture in group) fixture.Row = discovered.Single(row => row.FileUniqueId == fixture.Row.FileUniqueId);
        }
        acquired.Should().BeEquivalentTo(fixtures.Select(f => f.Row.FileUniqueId));
        var sourceCount = acquired.Count;
        var routing = new PageScanComponent.PageDiscovery
        {
            PublishingLayoutRuleVersion = 1, Pages = new(), EnrichmentInputs = new(), RemediationCodes = new(), SkipUserInformation = true,
        };
        var own = fixtures.Where(f => f.Row.ScanId == scan && f.Row.SiteUrl == Site).ToArray();
        foreach (var fixture in own) await PageScanComponent.RoutePhysicalPageAsync(routing, fixture.Row, fixture.List);
        await writer.UpdateExistingAsync(own.Select(f => f.Row));
        var excluded = own.Where(f => PublishingLayoutTypeEvidence.IsConfirmedMember(f.Row)).ToArray();
        excluded.Should().HaveCount(selection == "OtherSiteId" ? 4 : 3);
        excluded.Should().OnlyContain(f => f.ItemRequests == 0 && f.Row.PageType != "PublishingPage" && f.Row.AssessmentStatus == "ExcludedAsset");
        routing.Pages.Should().HaveCount(8);
        routing.EnrichmentInputs.Should().HaveCount(8);
        PageScanComponent.CountPages(routing.Pages).Should().Be((7, 0, 0, 0, 1));
        routing.ModernPageCounter.Should().Be(0);
        var extractionCalls = new List<string>();
        var parts = new List<ClassicPageWebPart>();
        foreach (var input in routing.EnrichmentInputs)
        {
            parts.AddRange(await PageScanComponent.ExtractAndMapWebPartsAsync(input, value =>
            {
                extractionCalls.Add(value.Page.PageUrl);
                excluded.Should().NotContain(f => f.Row.Url == value.Page.PageUrl);
                if (value.Page.PageUrl == envelope.Row.Url)
                {
                    value.WikiFieldHtml.Should().BeNull();
                    PageWebPartExtractor.ApplyPublishingMetadata(value.Page, fields);
                }
                else value.WikiFieldHtml.Should().Contain("Own body:");
                return Task.FromResult(PageWebPartExtractor.ToRows(value.Page, new()
                {
                    new() { Type = "Microsoft.SharePoint.WebPartPages.ContentEditorWebPart, Microsoft.SharePoint", Title = "Own body", ZoneId = "Main" },
                }, exportWebPartProperties: true));
            }));
        }
        extractionCalls.Should().BeEquivalentTo(routing.Pages.Select(p => p.PageUrl));
        foreach (var page in routing.Pages)
            PageScanComponent.ApplyDiscoveryState(page, own.Single(f => f.Row.Url == page.PageUrl).Row);
        using (var db = database.CreateContext())
        {
            await StorageManager.StorePageInformationAsync(db, routing.Pages);
            await StorageManager.StorePageWebPartsAsync(db, parts);
            foreach (var group in routing.Pages.GroupBy(p => p.WebUrl))
            {
                var counts = PageScanComponent.CountPages(group);
                db.ClassicWebSummaries.Add(new ClassicWebSummary { ScanId = scan, SiteUrl = Site, WebUrl = group.Key,
                    ClassicPages = group.Count(), ClassicPublishingPages = counts.Publishing, ClassicWikiPages = counts.Wiki });
            }
            await db.SaveChangesAsync();
            await StorageManager.ComputeAndStoreWebPageRollupsAsync(db, scan);
            await StorageManager.PopulatePublishingSiteSummaryAsync(db, scan);
        }
        await writer.FinalizeScanAsync(scan);
        acquired.Should().HaveCount(sourceCount, "reference resolution cannot cause target discovery or source reads");
        fixtures.Sum(f => f.StreamRequests).Should().Be(8);
        var directory = Path.Combine(Path.GetTempPath(), "family-pipeline-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            using var db = database.CreateContext();
            var inventory = await db.ClassicPageDiscoveries.Where(r => r.ScanId == scan).ToListAsync();
            var pages = await db.ClassicPages.Where(p => p.ScanId == scan).ToListAsync();
            AssertPages(pages);
            AssertInventory(inventory);
            await ReportManager.ExportClassicReportDataAsync(db, scan, directory, new CsvConfiguration(CultureInfo.InvariantCulture));
            AssertPages(ReadCsv<ClassicPage>(directory, "classicpages.csv"), true);
            AssertInventory(ReadCsv<ClassicPageDiscovery>(directory, "discovery.csv"));
            ReadCsv<ClassicPageWebPart>(directory, "classicpagewebparts.csv").Should().HaveCount(8)
                .And.OnlyContain(p => extractionCalls.Contains(p.PageUrl));
            var summaries = ReadCsv<ClassicWebSummary>(directory, "classicwebsummaries.csv");
            summaries.Sum(s => s.ClassicPages).Should().Be(8);
            summaries.Sum(s => s.ClassicPublishingPages).Should().Be(1);
            summaries.Sum(s => s.PagesWithWebParts).Should().Be(8);
            ReadCsv<ClassicPublishingSiteSummary>(directory, "classicpublishingsitesummaries.csv").Should().ContainSingle()
                .Which.NumberOfPages.Should().Be(1);
        }
        finally { Directory.Delete(directory, true); }

        void AssertPages(IEnumerable<ClassicPage> pages, bool csv = false)
        {
            pages.Select(p => p.PageUrl).Should().BeEquivalentTo(extractionCalls);
            var page = pages.Single(p => p.PageUrl == envelope.Row.Url);
            page.PageType.Should().Be("PublishingPage");
            page.Layout.Should().Be(selection == "NoMetadata" ? "" : "ArticleLeft");
            page.LayoutUrl.Should().Be(selection == "NoMetadata" ? csv ? "" : null : selectedUrl);
            page.LayoutReferenceStatus.Should().Be(expectedStatus);
            page.LayoutReferenceReason.Should().Be(expectedReason);
            page.WebPartCount.Should().Be(1);
            page.FileUniqueId.Should().Be(envelope.Row.FileUniqueId);
        }

        void AssertInventory(IEnumerable<ClassicPageDiscovery> inventory)
        {
            var physical = inventory.Where(r => r.RowType == "Page").ToArray();
            physical.Should().HaveCount(fixtures.Count(f => f.Row.ScanId == scan));
            foreach (var fixture in fixtures.Where(f => f.Row.ScanId == scan))
            {
                var row = physical.Single(r => r.RecordKey == fixture.Row.RecordKey);
                row.Should().BeEquivalentTo(fixture.Row, config => config.Excluding(r => r.PageType)
                    .Using<string>(value => (value.Subject ?? "").Should().Be(value.Expectation ?? "")).WhenTypeIs<string>()
                    .Using<DateTime>(value => value.Subject.Should().BeCloseTo(value.Expectation, TimeSpan.FromSeconds(1))).WhenTypeIs<DateTime>());
                row.PageTypeEvidenceOrigin.Should().Be("DeclaredSource");
                var observations = JsonSerializer.Deserialize<PublishingLayoutTypeEvidence.Observation[]>(row.PageTypeEvidenceJson);
                observations.Should().ContainSingle();
                if (row.PublishingLayoutFamily == "Member")
                {
                    row.PageTypeResolutionStatus.Should().Be("Resolved");
                    row.PageTypeSourceStatus.Should().Be("Available");
                    observations[0].SourceHash.Should().HaveLength(64);
                    observations[0].Ancestry.Last().Provenance.Should().Be("WellKnownPublishingLayoutIdentity:v1");
                }
                if (row.FileName is "Direct.aspx" or "Indirect.aspx")
                {
                    observations[0].Ancestry.Should().HaveCount(row.FileName == "Direct.aspx" ? 2 : 3);
                    observations[0].Ancestry.First().Provenance.Should().Contain("ECMA335:").And.Contain("SHA256=");
                }
            }
            foreach (var name in nonmembers) physical.Single(r => r.FileName == name + ".aspx").PublishingLayoutFamily.Should().Be("NonMember");
            foreach (var name in new[] { "Unavailable", "Denied", "Failed", "Incomplete", "Missing" })
            {
                var row = physical.Single(r => r.FileName == name + ".aspx");
                row.PublishingLayoutFamily.Should().Be("Unknown");
                row.AssetPurposeStatus.Should().Be("Unknown");
                row.PageTypeReason.Should().NotBe("NotEvaluated");
                row.PageTypeSourceStatus.Should().Be(name == "Unavailable" ? "Unknown" : name is "Denied" or "Failed" ? name : "Available");
            }
            physical.Single(r => r.FileName == "Direct.aspx").ContentTypeId.Should().BeNullOrEmpty();
            physical.Single(r => r.FileName == "article.aspx").DeclaredPageType.Should().Contain("TemplateRedirectionPage");
            inventory.Should().Contain(r => r.RowType == "Scope" && r.DiscoveryStatus == "Complete");
            inventory.Single(r => r.RowType == "Summary").ObservedChildCount.Should().Be(physical.Length);
        }
    }

    private sealed class PipelineProvider(ClassicPageDiscovery[] rows) : IAspxDiscoveryProvider
    {
        public DiscoveryScopeRegistration RootScope { get; } = new("web:" + rows[0].WebId, null, DiscoveryScopeKind.Web, null, rows[0].WebUrl, "replay");
        private DiscoveryScopeRegistration Folder => new("files:" + rows[0].WebId, RootScope.ScopeKey, DiscoveryScopeKind.Folder,
            DiscoverySourceKind.RawListLibraryFiles, rows[0].WebUrl, "replay");
        public Task<DiscoveryChildEnumerationResult> EnumerateChildrenAsync(DiscoveryScopeRegistration parent, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DiscoveryChildEnumerationResult(DiscoveryScopeKind.Folder, Array.Empty<DiscoveryChildExpectation>(),
                parent == RootScope ? new[] { Folder } : Array.Empty<DiscoveryScopeRegistration>(), DiscoveryTerminalOutcome.Complete, "replay"));
        public IRawDiscoverySource CreateRawSource(DiscoveryScopeRegistration surface) => new PipelineBatch(rows.Select(row =>
            new RawDiscoveryRecord(row.RecordKey, row.FileUniqueId.ToString(), surface.ScopeKey, row.FileName, row.Url, true, "replay",
                SiteCollectionId: row.SiteCollectionId, WebId: row.WebId, ListId: row.ListId, ListItemId: row.ListItemId,
                ContentTypeId: row.ContentTypeId, PageType: row.PageType)).ToArray());
        public void Dispose() { }
    }

    private sealed class PipelineBatch(RawDiscoveryRecord[] rows) : IRawDiscoverySource
    {
        public async IAsyncEnumerable<RawDiscoveryBatch> ReadBatchesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new(1, null, null, rows, true, DiscoveryTerminalOutcome.Complete);
        }
    }
}
