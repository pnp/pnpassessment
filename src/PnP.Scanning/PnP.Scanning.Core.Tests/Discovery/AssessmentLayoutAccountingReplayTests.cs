using CsvHelper.Configuration;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.SharePoint.Client;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using System.Globalization;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery;

public sealed partial class AssessmentPageMetadataReplayTests
{
    [Theory]
    [InlineData("resolved", "Resolved", "ConfirmedLayoutAsset")]
    [InlineData("absent", "Unresolved", "TargetNotDiscovered")]
    [InlineData("unknownTarget", "Unresolved", "TargetAssessmentUnknown")]
    [InlineData("unknownPurpose", "Unresolved", "TargetPurposeUnavailable")]
    [InlineData("missingMetadata", "Unresolved", "ReferenceMetadataMissing")]
    [InlineData("unusableMetadata", "Unresolved", "ReferenceMetadataUnusable")]
    [InlineData("deniedReference", "Unresolved", "ReferenceMetadataDenied")]
    public async Task Layout_inventory_routes_through_analysis_storage_rollups_and_native_reports(
        string scenario, string referenceStatus, string referenceReason)
    {
        const string layoutUrl = Web + "/_catalogs/masterpage/Article.aspx";
        const string unknownUrl = Web + "/_catalogs/masterpage/unknown.aspx";
        const string absentUrl = Web + "/_catalogs/masterpage/not-discovered.aspx";
        const string contentEditor = "Microsoft.SharePoint.WebPartPages.ContentEditorWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";
        const string scriptEditor = "Microsoft.SharePoint.WebPartPages.ScriptEditorWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";
        var scan = Guid.NewGuid();
        string referenceUrl = scenario switch
        {
            "absent" => absentUrl,
            "unknownTarget" or "unknownPurpose" => unknownUrl,
            "missingMetadata" or "unusableMetadata" => null,
            _ => layoutUrl,
        };
        var referenceFields = new Dictionary<string, object>();
        if (scenario != "missingMetadata")
            referenceFields[PublishingLayoutReference.FieldName] = new FieldUrlValue
            {
                Url = referenceUrl, Description = "ArticleLeft",
            };
        var fields = new Dictionary<string, object>(referenceFields)
        {
            ["ContentTypeId"] = PublishingContentType.ToLowerInvariant(),
            ["Title"] = "Real publishing page",
            ["Modified"] = new DateTime(2026, 1, 2),
        };
        var publishing = new MetadataFixture(scan, "subweb/Pages/article.aspx", fields);
        publishing.Row.WebUrl = Web + "/subweb";
        publishing.Row.WebId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var catalog = new MetadataFixture(scan, "_catalogs/masterpage/Article.aspx", new());
        catalog.Row.ContentTypeId = AspxAssetPurpose.LayoutContentType;
        catalog.Row.PageType = SharePointLiveAspxDiscoveryProvider.InferPageType(catalog.Row.ContentTypeId);
        AspxAssetPurpose.Apply(catalog.Row, catalog.Row.ContentTypeId);
        var outside = new MetadataFixture(scan, "Custom/layout.aspx", new()
        {
            ["ContentTypeId"] = AspxAssetPurpose.LayoutContentType.ToLowerInvariant() + "00aabbccddeeff00112233445566778899",
            ["WikiField"] = "<p>Layout body must never enter content analysis</p>",
        });
        outside.Row.AssessmentStatus = "Failed";
        AssessmentWebDiscovery.AddError(outside.Row, "PageMetadata", "MetadataFailed", "Retained asset failure");
        var unknown = new MetadataFixture(scan, "_catalogs/masterpage/unknown.aspx", new() { ["BSN"] = "custom" });
        unknown.Row.AssessmentStatus = scenario == "unknownPurpose" ? "Pending" : "Unknown";
        unknown.Row.ErrorDetail = "Content type unavailable; not proof of a layout";
        var fixtures = new[] { publishing, catalog, outside, unknown };
        var writer = new AssessmentDiscoveryWriter(database.CreateContext);
        var routed = new PageScanComponent.PageDiscovery
        {
            Pages = new(), EnrichmentInputs = new(), RemediationCodes = new(), SkipUserInformation = true,
        };

        // Process the subweb first, then the catalog web. Admission uses unfiltered physical
        // inventory and native metadata projection, not seeded ClassicPages/report tables.
        foreach (var fixture in fixtures)
        {
            await writer.WriteAsync(new[] { fixture.Row });
            await PageScanComponent.RoutePhysicalPageAsync(routed, fixture.Row, fixture.List);
            await writer.UpdateExistingAsync(new[] { fixture.Row });
        }
        routed.Pages.Select(page => page.PageUrl).Should().BeEquivalentTo(publishing.Row.Url, unknown.Row.Url);
        routed.EnrichmentInputs.Should().ContainSingle().Which.Page.PageUrl.Should().Be(publishing.Row.Url);
        routed.RemediationCodes.Should().BeEquivalentTo("CP3", "CP5");
        routed.ModernPageCounter.Should().Be(0);
        catalog.StreamRequests.Should().Be(0);
        outside.StreamRequests.Should().Be(1);
        new[] { catalog.Row, outside.Row }.Should().OnlyContain(row => row.PageType != "PublishingPage");

        var extractionCalls = new List<string>();
        var webParts = new List<ClassicPageWebPart>();
        foreach (var input in routed.EnrichmentInputs)
        {
            webParts.AddRange(await PageScanComponent.ExtractAndMapWebPartsAsync(input, value =>
            {
                extractionCalls.Add(value.Page.PageUrl);
                value.Page.PageUrl.Should().Be(publishing.Row.Url, "no asset may reach the live extraction boundary");
                value.WikiFieldHtml.Should().BeNull("a Publishing page must not acquire the referenced layout's body");
                // Replay only the CSOM acquisition result. Use the real publishing metadata,
                // row projection and mapping code; there is no target request or layout body.
                PageWebPartExtractor.ApplyPublishingMetadata(value.Page, referenceFields);
                if (scenario == "deniedReference")
                {
                    PublishingLayoutReference.RecordFailure(value.Page, new UnauthorizedAccessException());
                    publishing.Row.AssessmentStatus = "Denied";
                    AssessmentWebDiscovery.AddError(publishing.Row, "PageMetadata", "HTTP403", "Retained reference denial");
                }
                return Task.FromResult(PageWebPartExtractor.ToRows(value.Page, new()
                {
                    new() { Type = contentEditor, Title = "Content", ZoneId = "Main", Properties = new() { ["Content"] = "<p>Content-page body only</p>" } },
                    new() { Type = scriptEditor, Title = "Script", ZoneId = "Main", Order = 1 },
                }, exportWebPartProperties: true));
            }));
        }
        extractionCalls.Should().BeEquivalentTo(publishing.Row.Url);
        await writer.UpdateExistingAsync(fixtures.Select(fixture => fixture.Row));
        foreach (var page in routed.Pages)
            PageScanComponent.ApplyDiscoveryState(page, fixtures.Single(fixture => fixture.Row.Url == page.PageUrl).Row);

        using (var db = database.CreateContext())
        {
            await StorageManager.StorePageInformationAsync(db, routed.Pages);
            await StorageManager.StorePageWebPartsAsync(db, webParts);
            foreach (var web in fixtures.Select(fixture => fixture.Row.WebUrl).Distinct())
            {
                var counts = PageScanComponent.CountPages(routed.Pages.Where(page => page.WebUrl == web));
                counts.Should().Be(web == Web ? (0, 0, 0, 1, 0) : (0, 0, 0, 0, 1));
                db.ClassicWebSummaries.Add(new ClassicWebSummary
                {
                    ScanId = scan, SiteUrl = Site, WebUrl = web, Template = "STS#0",
                    ClassicPublishingPages = counts.Publishing, ClassicASPXPages = counts.Aspx,
                    ClassicPages = counts.Wiki + counts.Blog + counts.WebPart + counts.Aspx + counts.Publishing,
                });
            }
            await db.SaveChangesAsync();
            await StorageManager.ComputeAndStoreWebPageRollupsAsync(db, scan);
            await StorageManager.PopulateWebPartUniqueAsync(db, scan, PageScanComponent.MappingManager);
            await StorageManager.PopulatePublishingSiteSummaryAsync(db, scan);
        }
        await writer.WriteAsync(new[] { new ClassicPageDiscovery
        {
            ScanId = scan, RecordKey = "scope:denied", RowType = "Scope", ScopeType = "Container",
            SiteUrl = Site, WebUrl = Web, DiscoveryStatus = "Denied", ErrorCodes = "HTTP403",
            ErrorDetail = "Retained enumeration denial", ObservedAtUtc = DateTime.UtcNow,
        } });
        await writer.FinalizeScanAsync(scan);

        var directory = Path.Combine(Path.GetTempPath(), "layout-accounting-replay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var db = database.CreateContext();
            var stored = await db.ClassicPages.Where(page => page.ScanId == scan).ToListAsync();
            stored.Select(page => page.PageUrl).Should().BeEquivalentTo(publishing.Row.Url, unknown.Row.Url);
            AssertContent(stored.Single(page => page.PageUrl == publishing.Row.Url));
            var inventory = await db.ClassicPageDiscoveries.Where(row => row.ScanId == scan).ToListAsync();
            AssertInventory(inventory);
            inventory.Where(row => row.AssetPurpose == "PageLayout").Should().OnlyContain(row => row.PageType != "PublishingPage");
            var storedParts = await db.ClassicPageWebParts.Where(row => row.ScanId == scan).ToListAsync();
            storedParts.Should().HaveCount(2).And.OnlyContain(row => row.PageUrl == publishing.Row.Url);
            storedParts.Single(row => row.WebPartTitle == "Content").WebPartProperties.Should().Contain("Content-page body only");

            await ReportManager.ExportClassicReportDataAsync(db, scan, directory, new CsvConfiguration(CultureInfo.InvariantCulture));
            var exported = ReadCsv<ClassicPage>(directory, "classicpages.csv");
            exported.Select(page => page.PageUrl).Should().BeEquivalentTo(publishing.Row.Url, unknown.Row.Url);
            AssertContent(exported.Single(page => page.PageUrl == publishing.Row.Url), exported: true);
            AssertInventory(ReadCsv<ClassicPageDiscovery>(directory, "discovery.csv"));
            ReadCsv<ClassicPageWebPart>(directory, "classicpagewebparts.csv").Should().BeEquivalentTo(storedParts,
                config => config.Using<string>(value => value.Subject.Should().Be(value.Expectation ?? "")).WhenTypeIs<string>());
            var summaries = ReadCsv<ClassicWebSummary>(directory, "classicwebsummaries.csv");
            summaries.Sum(row => row.ClassicPublishingPages).Should().Be(1);
            summaries.Sum(row => row.ClassicPages).Should().Be(2);
            summaries.Sum(row => row.PagesWithWebParts).Should().Be(1);
            summaries.Sum(row => row.MappableWebPartPages).Should().Be(0);
            summaries.Sum(row => row.UnmappedWebPartPages).Should().Be(1);
            summaries.Single(row => row.WebUrl != Web).AvgMappingPercentage.Should().Be(50);
            summaries.Single(row => row.WebUrl == Web).PagesWithWebParts.Should().Be(0);
            var portal = ReadCsv<ClassicPublishingSiteSummary>(directory, "classicpublishingsitesummaries.csv").Single();
            portal.NumberOfPages.Should().Be(1);
            portal.NumberOfWebs.Should().Be(1);
            portal.UsedPageLayouts.Should().Be(scenario == "missingMetadata" ? "" : "ArticleLeft");
            portal.LastPageUpdateDate.Should().Be(new DateTime(2026, 1, 2));
            ReadCsv<ClassicWebPartUnique>(directory, "classicwebpartunique.csv").Should().HaveCount(2)
                .And.OnlyContain(row => row.PageCount == 1 && row.InMappingFile);
        }
        finally { Directory.Delete(directory, recursive: true); }

        void AssertContent(ClassicPage page, bool exported = false)
        {
            page.PageType.Should().Be("PublishingPage");
            page.Layout.Should().Be(scenario == "missingMetadata" ? "" : "ArticleLeft");
            page.LayoutUrl.Should().Be(referenceUrl ?? (exported ? "" : null));
            page.LayoutReferenceStatus.Should().Be(referenceStatus);
            page.LayoutReferenceReason.Should().Be(referenceReason);
            page.WebPartCount.Should().Be(2);
            page.MappingPercentage.Should().Be(50);
            page.UnmappedWebParts.Should().Be("Microsoft.SharePoint.WebPartPages.ScriptEditorWebPart");
            page.AssessmentStatus.Should().Be(scenario == "deniedReference" ? "Denied" : "Complete");
            page.WebId.Should().NotBe(catalog.Row.WebId.Value);
            page.SiteCollectionId.Should().Be(catalog.Row.SiteCollectionId);
            page.FileUniqueId.Should().Be(publishing.Row.FileUniqueId);
        }

        void AssertInventory(IEnumerable<ClassicPageDiscovery> rows)
        {
            var physical = rows.Where(row => row.RowType == "Page").ToArray();
            physical.Should().HaveCount(4);
            physical.Select(row => row.FileUniqueId).Should().BeEquivalentTo(fixtures.Select(fixture => fixture.Row.FileUniqueId));
            foreach (var fixture in fixtures)
            {
                var physicalRow = physical.Single(row => row.RecordKey == fixture.Row.RecordKey);
                physicalRow.ScanId.Should().Be(scan);
                physicalRow.SiteUrl.Should().Be(fixture.Row.SiteUrl);
                physicalRow.SiteCollectionId.Should().Be(fixture.Row.SiteCollectionId);
                physicalRow.WebUrl.Should().Be(fixture.Row.WebUrl);
                physicalRow.WebId.Should().Be(fixture.Row.WebId);
                physicalRow.ListId.Should().Be(fixture.Row.ListId);
                physicalRow.ListItemId.Should().Be(fixture.Row.ListItemId);
                physicalRow.FileUniqueId.Should().Be(fixture.Row.FileUniqueId);
                physicalRow.Url.Should().Be(fixture.Row.Url);
            }
            physical.Should().OnlyContain(row => row.DiscoveryStatus == "Discovered");
            physical.Where(row => row.AssetPurpose == "PageLayout").Should().HaveCount(2)
                .And.OnlyContain(row => row.AssetPurposeStatus == "Confirmed" && row.AssetPurposeReason.Contains("PageLayoutContentType"));
            physical.Single(row => row.Url == layoutUrl).AssessmentStatus.Should().Be("ExcludedAsset");
            physical.Single(row => row.Url == outside.Row.Url).AssessmentStatus.Should().Be("Failed");
            physical.Single(row => row.Url == outside.Row.Url).ErrorDetail.Should().Contain("Retained asset failure");
            var uncertain = physical.Single(row => row.Url == unknownUrl);
            uncertain.AssetPurpose.Should().Be("Unknown");
            uncertain.AssetPurposeStatus.Should().Be("Unknown");
            uncertain.AssetPurposeReason.Should().Contain("ContentTypeUnavailable");
            uncertain.AssessmentStatus.Should().Be(scenario == "unknownPurpose" ? "Complete" : "Unknown");
            uncertain.ErrorDetail.Should().Contain("Content type unavailable");
            rows.Single(row => row.RowType == "Scope").DiscoveryStatus.Should().Be("Denied");
            rows.Single(row => row.RowType == "Scope").ErrorDetail.Should().Be("Retained enumeration denial");
            rows.Single(row => row.RowType == "Summary").ObservedChildCount.Should().Be(4);
            rows.Single(row => row.RowType == "Summary").DiscoveryStatus.Should().Be("Unknown");
            if (scenario == "resolved")
            {
                var target = physical.Single(row => PublishingLayoutReference.NormalizeUrl(row.Url, row.SiteUrl) == referenceUrl);
                target.ScanId.Should().Be(scan);
                target.SiteUrl.Should().Be(publishing.Row.SiteUrl);
                target.WebUrl.Should().NotBe(publishing.Row.WebUrl);
            }
            if (scenario == "deniedReference")
                physical.Single(row => row.Url == publishing.Row.Url).ErrorDetail.Should().Contain("Retained reference denial");
        }
    }
}
