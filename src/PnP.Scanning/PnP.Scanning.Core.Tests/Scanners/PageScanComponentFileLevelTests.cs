using FluentAssertions;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Storage;
using Xunit;

namespace PnP.Scanning.Core.Tests.Scanners
{
    /// <summary>
    /// File-level assessment of a discovered ASPX whose list item cannot be read. The regression this
    /// covers is a blanket skip: every page whose owning list was unmatched or whose ListItemId was null
    /// was marked NotApplicable and never analysed, which on a full tenant scan was 57,268 of 62,231
    /// pages. A missing list item removes the item-dependent reads, not the page: the file still resolves
    /// by URL, which is all <c>PageWebPartExtractor.ExtractFromWebPartPageAsync</c> needs.
    ///
    /// The setup paths below were read from a live tenant, not invented. On the blog_Archive web of
    /// /sites/biatmicrosoft-ccd35-ad1dab8b, default.aspx, newsfeed.aspx and GettingStarted.aspx all have
    /// no list item (loading ListItemAllFields faults), yet each one's vti_setuppath and web part manager
    /// read from the file alone — newsfeed.aspx returning a Site Feed web part and GettingStarted.aspx a
    /// "Get started with your site" web part that the blanket skip discarded.
    /// </summary>
    public class PageScanComponentFileLevelTests
    {
        private const string Site = "https://contoso.sharepoint.com/sites/team";
        private const string Web = "/sites/team/blog_Archive";

        [Fact]
        public void File_without_a_list_item_is_assessed_instead_of_skipped()
        {
            var disc = Discovery();
            var row = ListBacked(Row("newsfeed.aspx", @"Features\SiteFeed\newsfeed.aspx"));

            PageScanComponent.AddFileLevelPage(disc, row, owningListUnresolved: false);

            row.AssessmentStatus.Should().Be("Complete").And.NotBe("NotApplicable");
            row.DiscoveryStatus.Should().Be("Discovered", "the file's established existence survives");
            disc.Pages.Should().ContainSingle().Which.PageUrl.Should().Be(row.Url);
            disc.EnrichmentInputs.Should().ContainSingle()
                .Which.FileLevelOnly.Should().BeTrue("the page must still reach a web part read");
        }

        [Theory]
        // The 4 pages in the scoped scan that belong to no list and are not stock view/form templates.
        [InlineData("default.aspx", @"SiteTemplates\STS\default.aspx")]
        [InlineData("newsfeed.aspx", @"Features\SiteFeed\newsfeed.aspx")]
        [InlineData("GettingStarted.aspx", @"Features\GettingStarted\GettingStarted.aspx")]
        [InlineData("default.aspx", @"Features\MobilityRedirect\default.aspx")]
        public void File_belonging_to_no_list_is_assessed_and_is_not_a_failure(string fileName, string setupPath)
        {
            // A null ListId is the normal state of a web-root file, not a lookup that came up empty, so
            // it is a confirmed structural absence of the same class as a null ListItemId. These are the
            // pages the live tenant showed returning real web parts; failing them inverts the intent.
            var disc = Discovery();
            var row = Row(fileName, setupPath);
            row.ListId.Should().BeNull();

            PageScanComponent.AddFileLevelPage(disc, row, owningListUnresolved: false);

            row.AssessmentStatus.Should().Be("Complete").And.NotBe("Failed");
            row.ErrorCodes.Should().Contain(PageScanComponent.ListAssociationAbsentCode)
                .And.NotContain(PageScanComponent.OwningListUnresolvedCode);
            row.ErrorStage.Should().Contain("PageItemMetadata");
            disc.EnrichmentInputs.Should().ContainSingle().Which.FileLevelOnly.Should().BeTrue();
        }

        [Fact]
        public void A_file_in_no_list_is_not_treated_as_an_unresolved_lookup()
        {
            // The defect this guards: both cases reach the page loop as a failed list match, and
            // collapsing them marked every web-root file Failed. In the scoped scan all 25 item-less
            // rows without a list were of the first kind, and none of the second.
            PageScanComponent.IsOwningListUnresolved(Row("default.aspx", ""), listMatched: false)
                .Should().BeFalse("a null ListId means the file belongs to no list, not that a lookup missed");
            PageScanComponent.IsOwningListUnresolved(ListBacked(Row("page.aspx", "")), listMatched: false)
                .Should().BeTrue("a named list the web did not return is a real inconsistency");
            PageScanComponent.IsOwningListUnresolved(ListBacked(Row("page.aspx", "")), listMatched: true)
                .Should().BeFalse();
        }

        [Fact]
        public void Absent_list_association_unresolved_list_and_absent_item_are_all_distinguishable()
        {
            // 1. The file belongs to no list at all.
            var noList = Row("newsfeed.aspx", @"Features\SiteFeed\newsfeed.aspx");
            PageScanComponent.AddFileLevelPage(Discovery(), noList, owningListUnresolved: false);

            // 2. The file names an owning list that the web did not return, and does carry an item id.
            var unresolvedList = ListBacked(Row("default.aspx", @"SiteTemplates\STS\default.aspx"));
            unresolvedList.ListItemId = 12;
            PageScanComponent.AddFileLevelPage(Discovery(), unresolvedList, owningListUnresolved: true);

            // 3. The owning list was matched; the server confirmed there is no backing item.
            var absentItem = ListBacked(Row("page.aspx", ""));
            PageScanComponent.AddFileLevelPage(Discovery(), absentItem, owningListUnresolved: false);

            noList.ErrorCodes.Should().Contain(PageScanComponent.ListAssociationAbsentCode)
                .And.NotContain(PageScanComponent.OwningListUnresolvedCode);
            unresolvedList.ErrorCodes.Should().Contain(PageScanComponent.OwningListUnresolvedCode)
                .And.NotContain(PageScanComponent.ListAssociationAbsentCode)
                .And.NotContain(PageScanComponent.ListItemAbsentCode);
            absentItem.ErrorCodes.Should().Contain(PageScanComponent.ListItemAbsentCode)
                .And.NotContain(PageScanComponent.ListAssociationAbsentCode)
                .And.NotContain(PageScanComponent.OwningListUnresolvedCode);

            // Only an unread association leaves the assessment incomplete; absences do not.
            noList.AssessmentStatus.Should().Be("Complete");
            unresolvedList.AssessmentStatus.Should().Be("Failed");
            absentItem.AssessmentStatus.Should().Be("Complete");
        }

        [Fact]
        public void Unresolved_owning_list_and_absent_item_at_once_record_both_reasons()
        {
            var row = ListBacked(Row("GettingStarted.aspx", @"Features\GettingStarted\GettingStarted.aspx"));

            PageScanComponent.AddFileLevelPage(Discovery(), row, owningListUnresolved: true);

            row.ErrorCodes.Should().Contain(PageScanComponent.OwningListUnresolvedCode)
                .And.Contain(PageScanComponent.ListItemAbsentCode)
                .And.NotContain(PageScanComponent.ListAssociationAbsentCode);
            row.AssessmentStatus.Should().Be("Failed");
        }

        [Theory]
        // Every list provisions exactly one view page and one form page from these templates.
        [InlineData(@"pages\viewpage.aspx")]
        [InlineData(@"GLOBAL\pages\form.aspx")]
        public void Stock_view_and_form_templates_are_not_assessed_as_content_pages(string setupPath)
        {
            var disc = Discovery();
            var row = Row("AllItems.aspx", setupPath);

            PageScanComponent.AddFileLevelPage(disc, row, owningListUnresolved: false);

            // NotApplicable here is a server-recorded provisioning fact, carried as its own evidence code.
            row.AssessmentStatus.Should().Be("NotApplicable");
            row.ErrorCodes.Should().Contain(PageScanComponent.SystemPageTemplateCode)
                .And.Contain(PageScanComponent.ListItemAbsentCode);
            row.DiscoveryStatus.Should().Be("Discovered");
            disc.Pages.Should().BeEmpty();
            disc.EnrichmentInputs.Should().BeEmpty("there is no page content surface to read");
        }

        [Theory]
        // An empty setup path means unghosted: user authored or user uploaded.
        [InlineData("")]
        [InlineData(null)]
        // Real entry pages the blanket skip discarded. None of these map to a known template.
        [InlineData(@"Features\GroupHomepage\Home.aspx")]
        [InlineData(@"Features\SiteFeed\newsfeed.aspx")]
        [InlineData(@"Features\MobilityRedirect\default.aspx")]
        // A file name containing "form" is not a list form unless it ghosts from the form template.
        [InlineData(@"contoso\templates\myform.aspx")]
        public void Unmapped_setup_paths_are_assessed_rather_than_assumed_to_be_system_pages(string setupPath)
        {
            var disc = Discovery();
            var row = Row("default.aspx", setupPath);

            PageScanComponent.AddFileLevelPage(disc, row, owningListUnresolved: false);

            row.AssessmentStatus.Should().Be("Complete").And.NotBe("NotApplicable");
            row.ErrorCodes.Should().NotContain(PageScanComponent.SystemPageTemplateCode);
            // The residual type: honest about not knowing the page's purpose, and still read.
            disc.Pages.Should().ContainSingle().Which.PageType.Should().Be(PageScanComponent.ASPXPage);
            disc.RemediationCodes.Should().Equal("CP5");
            disc.EnrichmentInputs.Should().ContainSingle().Which.FileLevelOnly.Should().BeTrue();
        }

        [Theory]
        [InlineData(@"1033\STS\doctemp\smartpgs\spstd1.aspx")]
        [InlineData(@"1033\STS\doctemp\smartpgs\bpstd.aspx")]
        // A web's root default.aspx ghosts from a site-definition home page template. These are web part
        // page layouts, so the entry page this route recovers is typed as one rather than as a residual.
        [InlineData(@"SiteTemplates\STS\default.aspx")]
        [InlineData(@"SiteTemplates\STS\defaultdws.aspx")]
        [InlineData(@"SiteTemplates\PROJECTSITE\default.aspx")]
        [InlineData(@"Features\BDR\default.aspx")]
        public void Proven_web_part_page_template_is_typed_as_a_web_part_page(string setupPath)
        {
            var disc = Discovery();

            PageScanComponent.AddFileLevelPage(disc, Row("page.aspx", setupPath), owningListUnresolved: false);

            disc.Pages.Should().ContainSingle().Which.PageType.Should().Be(PageScanComponent.WebPartPage);
            disc.RemediationCodes.Should().Equal("CP1");
        }

        [Fact]
        public void File_level_page_carries_file_evidence_and_fabricates_no_list_identity()
        {
            var disc = Discovery();
            var row = Row("GettingStarted.aspx", @"Features\GettingStarted\GettingStarted.aspx");
            row.HomePage = true;

            PageScanComponent.AddFileLevelPage(disc, row, owningListUnresolved: false);

            var page = disc.Pages.Should().ContainSingle().Subject;
            page.ScanId.Should().Be(row.ScanId);
            page.SiteUrl.Should().Be(Site);
            page.WebUrl.Should().Be(Web);
            page.PageUrl.Should().Be(row.Url);
            page.PageName.Should().Be("GettingStarted");
            page.SiteCollectionId.Should().Be(row.SiteCollectionId);
            page.WebId.Should().Be(row.WebId);
            page.FileUniqueId.Should().Be(row.FileUniqueId);
            page.HomePage.Should().BeTrue();

            // Nothing item sourced is invented to fill the gap.
            page.ListId.Should().Be(Guid.Empty);
            page.ListItemId.Should().BeNull();
            page.ListUrl.Should().BeNull();
            page.ListTitle.Should().BeNull();
            page.ModifiedBy.Should().BeNull();
            page.ModifiedAt.Should().Be(default);
        }

        [Fact]
        public void File_without_a_server_relative_url_makes_no_file_level_claim()
        {
            var disc = Discovery();
            var row = Row("orphan.aspx", "");
            row.Url = null;

            PageScanComponent.AddFileLevelPage(disc, row, owningListUnresolved: false);

            row.AssessmentStatus.Should().Be("Failed");
            row.ErrorCodes.Should().Contain(DiscoveryGapCodes.IdentityMissing);
            disc.Pages.Should().BeEmpty();
        }

        [Fact]
        public async Task File_level_page_is_routed_to_a_server_read_never_to_an_empty_inventory()
        {
            // Dispatching on PageType alone would send an ASPXPage to the "no web parts" arm and report
            // an unread page as empty. A file-level page must reach the web part manager instead, which
            // without a CSOM context can only fail — the point being that it is never silently empty.
            var fileLevel = () => PageScanComponent.ExtractWebPartsAsync(null,
                Input(PageScanComponent.ASPXPage, fileLevelOnly: true), false);
            await fileLevel.Should().ThrowAsync<NullReferenceException>();

            // An item-backed ASPXPage keeps its existing behaviour: it carries no web part surface and
            // was never added for enrichment in the first place.
            (await PageScanComponent.ExtractWebPartsAsync(null,
                Input(PageScanComponent.ASPXPage, fileLevelOnly: false), false)).Should().BeEmpty();
        }

        [Fact]
        public void Empty_file_level_inventory_is_not_rolled_up_as_fully_mappable()
        {
            // The legacy empty-page convention scores a page with no web parts as 100% mappable. That is
            // only sound when the page's whole content surface was read, which a file-level read is not.
            PageScanComponent.ShouldRollUpMapping(Input(PageScanComponent.ASPXPage, fileLevelOnly: true), 0)
                .Should().BeFalse();
            PageScanComponent.ShouldRollUpMapping(Input(PageScanComponent.ASPXPage, fileLevelOnly: true), 1)
                .Should().BeTrue();
            // Item-backed pages are untouched: their empty inventory still rolls up as before.
            PageScanComponent.ShouldRollUpMapping(Input(PageScanComponent.WikiPage, fileLevelOnly: false), 0)
                .Should().BeTrue();
        }

        [Fact]
        public void Failed_extraction_is_recorded_as_a_failure_not_as_zero_web_parts()
        {
            var disc = Discovery();
            var row = Row("default.aspx", @"SiteTemplates\STS\default.aspx");
            PageScanComponent.AddFileLevelPage(disc, row, owningListUnresolved: false);
            var page = disc.Pages.Single();

            // Replay what the enrichment loop does when the web part read throws: the readiness rollup is
            // never reached, so the page cannot be published as an empty, fully mappable page.
            row.AssessmentStatus = "Failed";
            AssessmentWebDiscovery.AddError(row, "WebPartAssessment", "HTTP403", "access denied reading web parts");
            PageScanComponent.ApplyDiscoveryState(page, row);

            page.AssessmentStatus.Should().Be("Failed");
            page.WebPartCount.Should().Be(0);
            page.MappingPercentage.Should().Be(0, "an unread page is not 100% mappable");
            page.UnmappedWebParts.Should().BeNull();
            page.DiscoveryStatus.Should().Be("Discovered", "the file still exists");
            page.FileUniqueId.Should().Be(row.FileUniqueId);
            row.ErrorCodes.Should().Contain("HTTP403").And.Contain(PageScanComponent.ListItemAbsentCode);
        }

        [Theory]
        // No page content surface of their own, so no extractor applies.
        [InlineData(@"pages\viewpage.aspx", null)]
        [InlineData(@"pages\form.aspx", null)]
        // A proven web part page template: its zones and its layout then read exactly.
        [InlineData(@"1033\STS\doctemp\smartpgs\spstd3.aspx", PageScanComponent.WebPartPage)]
        // Unghosted and unrecognised alike stay the residual type, and are still read.
        [InlineData("", PageScanComponent.ASPXPage)]
        [InlineData(@"Features\GroupHomepage\Home.aspx", PageScanComponent.ASPXPage)]
        // A wiki or blog template is recognised, but without the item there is no WikiField or post
        // content to read, so claiming either type would overstate what was actually assessed.
        [InlineData(@"DocumentTemplates\wkpstd.aspx", PageScanComponent.ASPXPage)]
        [InlineData(@"Features\BlogContent\Post.aspx", PageScanComponent.ASPXPage)]
        public void File_level_page_type_selects_only_an_applicable_extractor(string setupPath, string expected)
        {
            PageScanComponent.FileLevelPageType(SetupPathClassifier.Classify(setupPath)).Should().Be(expected);
        }

        private static PageScanComponent.PageDiscovery Discovery() => new()
        {
            Pages = new List<ClassicPage>(),
            RemediationCodes = new HashSet<string>(),
            EnrichmentInputs = new List<PageScanComponent.PageEnrichmentInput>(),
        };

        private static PageScanComponent.PageEnrichmentInput Input(string pageType, bool fileLevelOnly) => new()
        {
            Page = new ClassicPage { PageUrl = Web + "/page.aspx", PageType = pageType },
            FileLevelOnly = fileLevelOnly,
        };

        // Gives the row an owning list, for the cases where the file is list-backed. Left off by default
        // because the population this route recovers is web-root files, which have no ListId at all.
        private static ClassicPageDiscovery ListBacked(ClassicPageDiscovery row)
        {
            row.ListId = Guid.Parse("44444444-4444-4444-4444-444444444444");
            return row;
        }

        private static ClassicPageDiscovery Row(string fileName, string setupPath) => new()
        {
            ScanId = Guid.NewGuid(),
            SiteUrl = Site,
            WebUrl = Web,
            RecordKey = "page:" + fileName,
            RowType = "Page",
            ScopeType = "File",
            Url = Web + "/" + fileName,
            FileName = fileName,
            SiteCollectionId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            WebId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            FileUniqueId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            SetupPath = setupPath,
            DiscoveryStatus = "Discovered",
            ObservedAtUtc = DateTime.UtcNow,
        };
    }
}
