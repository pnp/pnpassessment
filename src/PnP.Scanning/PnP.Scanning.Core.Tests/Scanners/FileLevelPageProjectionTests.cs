using FluentAssertions;
using PnP.Scanning.Core.ClassicPages.Analysis.Page;
using PnP.Scanning.Core.ClassicPages.Contracts.Page;
using PnP.Scanning.Core.ClassicPages.Contracts.Shared;
using PnP.Scanning.Core.ClassicPages.Contracts.Web;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Scanners.WebPartMapping;
using Xunit;

namespace PnP.Scanning.Core.Tests.Scanners;

/// <summary>Verifies native file-level assessment without inventing backing items or complete readiness.</summary>
public sealed class FileLevelPageProjectionTests
{
    private const string Site = "https://contoso.sharepoint.com/sites/team";
    private const string Web = "/sites/team/subweb";
    private static readonly string[] PagesLibrary = [Web + "/Pages"];

    /// <summary>Retains files without items and chooses only the extractor justified by their setup family.</summary>
    [Theory]
    [InlineData("SiteTemplates\\STS\\default.aspx", ClassicPageRules.WebPartPage)]
    [InlineData("SiteTemplates\\STS\\defaultdws.aspx", ClassicPageRules.WebPartPage)]
    [InlineData("SiteTemplates\\PROJECTSITE\\default.aspx", ClassicPageRules.WebPartPage)]
    [InlineData("Features\\BDR\\default.aspx", ClassicPageRules.WebPartPage)]
    [InlineData("1033\\STS\\doctemp\\smartpgs\\spstd1.aspx", ClassicPageRules.WebPartPage)]
    [InlineData("1033\\STS\\doctemp\\smartpgs\\bpstd.aspx", ClassicPageRules.WebPartPage)]
    [InlineData("Features\\SiteFeed\\newsfeed.aspx", ClassicPageRules.ASPXPage)]
    [InlineData("Features\\GettingStarted\\GettingStarted.aspx", ClassicPageRules.ASPXPage)]
    [InlineData("Features\\MobilityRedirect\\default.aspx", ClassicPageRules.ASPXPage)]
    [InlineData("Features\\GroupHomepage\\Home.aspx", ClassicPageRules.ASPXPage)]
    [InlineData("DocumentTemplates\\wkpstd.aspx", ClassicPageRules.ASPXPage)]
    [InlineData("Features\\BlogContent\\Post.aspx", ClassicPageRules.ASPXPage)]
    [InlineData("contoso\\templates\\myform.aspx", ClassicPageRules.ASPXPage)]
    [InlineData("", ClassicPageRules.ASPXPage)]
    [InlineData(null, ClassicPageRules.ASPXPage)]
    public void Missing_item_does_not_discard_physical_content(string setupPath, string expectedType)
    {
        DiscoveryEvidence row = Row(setupPath);
        row.HomePage = true;
        ClassicPageRow page = FileLevelPageProjection.Project(
            row,
            FileLevelPageProjection.ListAssociationState.Resolved);

        page.PageType.Should().Be(expectedType);
        page.RemediationCode.Should().Be(expectedType == ClassicPageRules.WebPartPage ? "CP1" : "CP5");
        page.ScanId.Should().Be(row.ScanId);
        page.SiteCollectionId.Should().Be(row.SiteCollectionId);
        page.WebId.Should().Be(row.WebId);
        page.FileUniqueId.Should().Be(row.FileUniqueId);
        page.PageUrl.Should().Be(row.Url);
        page.PageName.Should().Be("page");
        page.HomePage.Should().BeTrue();
        page.ListId.Should().BeNull();
        page.ListItemId.Should().BeNull();
        page.ListUrl.Should().BeNull();
        page.ListTitle.Should().BeNull();
        page.ModifiedBy.Should().BeNull();
        page.ModifiedAt.Should().Be(default);
        row.AssessmentStatus.Should().Be("Complete");
        row.DiscoveryStatus.Should().Be("Discovered");
        row.ErrorCodes.Should()
            .Contain(FileLevelPageProjection.ListAssociationAbsentCode)
            .And.Contain(FileLevelPageProjection.ListItemAbsentCode);
    }

    /// <summary>Excludes proven stock forms and views while preserving their existence and diagnostics.</summary>
    [Theory]
    [InlineData("pages\\viewpage.aspx")]
    [InlineData("GLOBAL\\pages\\form.aspx")]
    public void Stock_forms_and_views_have_no_independent_content_surface(string setupPath)
    {
        DiscoveryEvidence row = Row(setupPath);
        FileLevelPageProjection.Project(row, FileLevelPageProjection.ListAssociationState.Resolved).Should().BeNull();
        row.AssessmentStatus.Should().Be("NotApplicable");
        row.DiscoveryStatus.Should().Be("Discovered");
        row.ErrorCodes.Should()
            .Contain(FileLevelPageProjection.SystemPageTemplateCode)
            .And.Contain(FileLevelPageProjection.ListItemAbsentCode);
    }

    /// <summary>Separates an absent association, an unmatched named owner and an unobserved list-backed association.</summary>
    [Theory]
    [InlineData(false, false, false, "Resolved")]
    [InlineData(true, false, false, "OwningListUnresolved")]
    [InlineData(true, true, false, "Resolved")]
    [InlineData(false, false, true, "Unknown")]
    public void Association_states_remain_distinct(bool namedList, bool matched, bool insideList, string expected)
    {
        DiscoveryEvidence row = Row("");
        if (namedList) row.ListId = Guid.NewGuid();
        if (insideList) row.Url = Web + "/Pages/page.aspx";
        FileLevelPageProjection.ListAssociationState actual =
            FileLevelPageProjection.ClassifyListAssociation(row, matched, PagesLibrary);
        actual.ToString().Should().Be(expected);
        FileLevelPageProjection.Project(row, actual).Should().NotBeNull();
        row.AssessmentStatus.Should()
            .Be(actual == FileLevelPageProjection.ListAssociationState.Resolved ? "Complete" : "Failed");
        if (actual == FileLevelPageProjection.ListAssociationState.OwningListUnresolved)
            row.ErrorCodes.Should()
                .Contain(FileLevelPageProjection.OwningListUnresolvedCode)
                .And.NotContain(FileLevelPageProjection.ListAssociationAbsentCode);
        if (actual == FileLevelPageProjection.ListAssociationState.Unknown)
            row.ErrorCodes.Should()
                .Contain(FileLevelPageProjection.ListAssociationUnknownCode)
                .And.NotContain(FileLevelPageProjection.ListAssociationAbsentCode);
    }

    /// <summary>Does not report an absent item when the native observation actually contains an item ID.</summary>
    [Fact]
    public void Unmatched_owner_with_observed_item_retains_only_the_owner_gap()
    {
        DiscoveryEvidence row = Row("");
        row.ListId = Guid.NewGuid();
        row.ListItemId = 12;
        FileLevelPageProjection.Project(row, FileLevelPageProjection.ListAssociationState.OwningListUnresolved);
        row.ErrorCodes.Should()
            .Contain(FileLevelPageProjection.OwningListUnresolvedCode)
            .And.NotContain(FileLevelPageProjection.ListItemAbsentCode);
    }

    /// <summary>Requires a physical server-relative path before claiming a file-level page.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1")]
    public void Missing_physical_locator_produces_a_failure_not_a_page(string url)
    {
        DiscoveryEvidence row = Row("");
        row.Url = url;
        FileLevelPageProjection.Project(row, FileLevelPageProjection.ListAssociationState.Resolved).Should().BeNull();
        row.AssessmentStatus.Should().Be("Failed");
        row.ErrorCodes.Should().Contain(DiscoveryGapCodes.IdentityMissing);
    }

    /// <summary>Matches list roots case-insensitively at a folder boundary rather than a shared textual prefix.</summary>
    [Theory]
    [InlineData(Web + "/Pages/foo.aspx", true)]
    [InlineData(Web + "/Pages/sub/foo.aspx", true)]
    [InlineData(Web + "/PAGES/foo.aspx", true)]
    [InlineData(Web + "/PagesArchive/foo.aspx", false)]
    [InlineData(Web + "/default.aspx", false)]
    [InlineData(Web + "/Pages", false)]
    [InlineData(null, false)]
    public void List_containment_respects_directory_boundaries(string url, bool expected) =>
        FileLevelPageProjection.IsInsideAnyListRootFolder(url, PagesLibrary).Should().Be(expected);

    /// <summary>Does not infer list membership from an unavailable or empty list-root set.</summary>
    [Fact]
    public void Empty_list_roots_do_not_invent_an_owner()
    {
        FileLevelPageProjection.IsInsideAnyListRootFolder(Web + "/Pages/foo.aspx", null).Should().BeFalse();
        FileLevelPageProjection.IsInsideAnyListRootFolder(Web + "/Pages/foo.aspx", []).Should().BeFalse();
        FileLevelPageProjection.IsInsideAnyListRootFolder(Web + "/Pages/foo.aspx", ["", "   "]).Should().BeFalse();
    }

    /// <summary>Never promotes empty or unread file-only zones to complete 100-percent page readiness.</summary>
    [Theory]
    [InlineData("Complete")]
    [InlineData("Denied")]
    [InlineData("NotAttempted")]
    public void Empty_or_unread_file_level_inventory_has_no_full_readiness(string status)
    {
        DiscoveryEvidence row = Row("SiteTemplates\\STS\\default.aspx");
        FilePageAnalysis output = Analyze(row, new(status));
        output.Page.Should().NotBeNull();
        output.Page.MappingPercentage.Should().Be(0);
        output.Page.WebPartCount.Should().Be(0);
        output.Page.UnmappedWebParts.Should().BeNull();
        output.Page.Identity.Should().Be(PageIdentity.File(row.SiteCollectionId.Value, row.FileUniqueId.Value));
        output.Discovery.DiscoveryStatus.Should().Be("Discovered");
        if (status != "Complete") output.Page.AssessmentStatus.Should().Be("Failed");
    }

    /// <summary>Builds native analysis inputs with no online context or fabricated list item.</summary>
    private static FilePageAnalysis Analyze(DiscoveryEvidence row, SourceReadState partsState)
    {
        WebFacts web = new(
            Site,
            Web,
            "STS#0",
            row.SiteCollectionId.Value,
            row.WebId.Value,
            default,
            [],
            [],
            null,
            SourceReadState.NotAttempted,
            null,
            SourceReadState.NotAttempted,
            null,
            0,
            null,
            SourceReadState.NotAttempted,
            SourceReadState.Complete);
        FilePageEvidence source = new(
            Site,
            Web,
            row.RecordKey,
            null,
            null,
            null,
            [],
            [],
            [],
            SourceReadState.NotAttempted,
            partsState,
            null,
            SourceReadState.NotAttempted,
            FileLevelOnly: true,
            ListRootFolderUrls: []);
        return FilePageProjection.Analyze(
            row.ScanId,
            web,
            row,
            source,
            new(false, true, true, false, 14, null),
            new WebPartMappingManager());
    }

    /// <summary>Creates synthetic physical discovery evidence with real native GUIDs and no backing item.</summary>
    private static DiscoveryEvidence Row(string setupPath) =>
        new()
        {
            ScanId = Guid.NewGuid(),
            SiteUrl = Site,
            WebUrl = Web,
            RecordKey = "page:fixture",
            RowType = DiscoveryRowType.Page,
            ScopeType = "File",
            Url = Web + "/page.aspx",
            FileName = "page.aspx",
            SiteCollectionId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            WebId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            FileUniqueId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            SetupPath = setupPath,
            DiscoveryStatus = "Discovered",
            ObservedAtUtc = DateTime.UtcNow,
        };
}