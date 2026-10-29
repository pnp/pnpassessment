using System.Globalization;
using System.Reflection;
using System.Xml.Linq;
using CsvHelper;
using CsvHelper.Configuration;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PnP.Core.Model;
using PnP.Core.Model.SharePoint;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Tests.Fixtures;
using Xunit;
using OfflineSdk = PnP.Scanning.Core.Tests.Discovery.SharedDiscoveryContextTests.OfflineSdk;

namespace PnP.Scanning.Core.Tests.Scanners.Pages;

/// <summary>
/// Seeds real Posts/custom-form list metadata and native item-stream responses, then exercises the
/// production legacy assessment, page persistence, summary writer and native CSV export. Only the SDK
/// boundary is a strict fake; no assessment page, CP4 classification or blog counter is hand-seeded.
/// </summary>
[Trait("Category", "ClassicDiscoveryIntegration")]
public sealed class LegacyDisplayFormAssessmentTests : IClassFixture<ScanContextFixture>
{
    private const string Site = "https://contoso.sharepoint.com/sites/reuse";
    private const string Web = "/sites/reuse/blog";
    private const string WebAddress = Site + "/blog";
    private const string PostType = "0x0110";
    private const string SpecializedPostType = "0x0110007C4E";
    private const string StockForm = "Lists/Posts/DispForm.aspx";
    private const string PostForm = "Lists/Posts/Post.aspx";
    private const string SpecializedPostForm = "~site/Lists/Posts/Special.aspx";
    private const string QuotedPostTitle = "Post, \"quoted\"\nbody";
    private static readonly DateTime ModifiedAt = new(2026, 10, 20, 12, 30, 0, DateTimeKind.Utc);
    private readonly ScanContextFixture database;

    /// <summary>Retains the isolated in-memory database fixture for the production persistence replay.</summary>
    public LegacyDisplayFormAssessmentTests(ScanContextFixture database)
    {
        this.database = database;
    }

    /// <summary>
    /// Produces CP4/BlogPage through actual item assessment, persists/exports them and their real blog
    /// rollup, and proves N item addresses do not become N inventory files or reference bindings.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Seeded_posts_and_custom_forms_reach_native_report_and_blog_rollup_without_growing_inventory(
        bool skipUserInformation)
    {
        var scan = Guid.NewGuid();
        var posts = Posts();
        var custom = new ListFixture(ListBaseType.GenericList, ListTemplateType.GenericList, "Articles",
            "~sitecollection/Lists/Articles/Article.aspx", Array.Empty<(string, string)>(),
            new[] { Item(7, "0x01", "Custom article") });
        var ordinary = new ListFixture(ListBaseType.GenericList, ListTemplateType.GenericList, "Tasks",
            "Lists/Tasks/DispForm.aspx", Array.Empty<(string, string)>(),
            new[] { Item(99, "0x01", "Not a content page") });
        var library = new ListFixture(ListBaseType.DocumentLibrary, ListTemplateType.DocumentLibrary, "Documents",
            "Lists/Documents/Custom.aspx", Array.Empty<(string, string)>(),
            new[] { Item(99, PostType, "Not an item-only page") });
        var physical = PhysicalForm(scan, posts.Id);
        var writer = new AssessmentDiscoveryWriter(database.CreateContext);
        await writer.WriteAsync(new[] { physical });
        using (var seed = database.CreateContext())
        {
            seed.ClassicAspxInventories.Add(new ClassicAspxInventory
            {
                ScanId = scan,
                AspxKey = ClassicAspxInventory.CreateTenantFileKey(physical.SiteCollectionId.Value, physical.FileUniqueId.Value),
                SiteCollectionId = physical.SiteCollectionId.Value,
                WebId = physical.WebId,
                ListId = posts.Id,
                FileUniqueId = physical.FileUniqueId.Value,
                SiteUrl = Site,
                WebUrl = Web,
                Url = physical.Url,
                FileName = "Post.aspx",
                FirstObservedAtUtc = ModifiedAt,
                ObservedAtUtc = ModifiedAt,
            });
            await seed.SaveChangesAsync();
        }

        var assessment = NewAssessment(scan, skipUserInformation);
        await PageScanComponent.AddDisplayFormPagesAsync(assessment, new Uri(WebAddress),
            new[] { posts.List, custom.List, ordinary.List, library.List }, CancellationToken.None);
        var pages = assessment.Pages;
        assessment.EnrichmentInputs.Should().BeEmpty("item web parts belong to the shared form, not its item addresses");
        assessment.RemediationCodes.Should().BeEquivalentTo("CP4", "CP5");

        pages.Should().HaveCount(3);
        posts.Requests.Should().HaveCount(2);
        posts.Requests[0].Paging.Should().BeNull();
        posts.Requests[1].Paging.Should().Be("Paged=TRUE&p_ID=7");
        ordinary.Requests.Should().BeEmpty("stock-only lists must not enumerate any items");
        library.Requests.Should().BeEmpty("document library items already belong to physical file assessment");
        var blogs = pages.Where(page => page.PageType == PageScanComponent.BlogPage).ToArray();
        blogs.Should().HaveCount(2).And.OnlyContain(page => page.RemediationCode == "CP4");
        blogs.Select(page => page.PageUrl).Should().BeEquivalentTo(
            Web + "/Lists/Posts/Special.aspx?ID=7", Web + "/Lists/Posts/Post.aspx?ID=3");
        blogs.Single(page => page.ListItemId == 7).PageName.Should().Be(QuotedPostTitle);
        pages.Single(page => page.ListId == custom.Id).Should().Match<ClassicPage>(page =>
            page.PageType == PageScanComponent.ASPXPage && page.RemediationCode == "CP5" &&
            page.PageUrl == "/sites/reuse/Lists/Articles/Article.aspx?ID=7");
        pages.Should().OnlyContain(page => page.HomePage == false && page.AssessmentStatus == "Complete" &&
            page.DiscoveryStatus == "Discovered" && page.FileUniqueId == null &&
            page.SiteCollectionId == null && page.WebId == null && page.ModifiedAt == ModifiedAt);
        pages.Select(page => page.ModifiedBy).Should().OnlyContain(value =>
            value == (skipUserInformation ? null : "author@contoso.com"));
        pages.Should().OnlyContain(page => page.WebPartCount == 0 && page.MappingPercentage == 0 &&
            page.CustomizedPageStatus == null && page.LayoutUrl == null);
        foreach (var request in posts.Requests.Concat(custom.Requests))
        {
            var fields = XDocument.Parse(request.ViewXml).Root.Element("ViewFields")
                .Elements("FieldRef").Select(field => field.Attribute("Name").Value).ToArray();
            fields.Contains("Editor").Should().Be(!skipUserInformation);
            fields.Should().NotContain(new[] { "FileRef", "FileLeafRef", "UniqueId", "File_x0020_Type", "WikiField" });
        }

        using var sdk = new OfflineSdk();
        var context = await sdk.CreateAsync();
        var counts = PageScanComponent.CountAssessedPages(pages);
        using (var store = database.CreateContext())
        {
            await StorageManager.StorePageInformationAsync(store, pages);
            await StorageManager.StorePageSummaryAsync(store, scan, Site, Web, "BLOG#0", context,
                assessment.RemediationCodes, assessment.ModernPageCounter, counts.Wiki, counts.Blog,
                counts.WebPart, counts.Aspx, counts.Publishing);
            await StorageManager.ComputeAndStoreWebPageRollupsAsync(store, scan);
        }

        using var read = database.CreateContext();
        var stored = await read.ClassicPages.Where(page => page.ScanId == scan).ToListAsync();
        stored.Should().HaveCount(3).And.OnlyContain(page => page.FileUniqueId == null);
        var summary = await read.ClassicWebSummaries.SingleAsync(value => value.ScanId == scan);
        summary.ClassicBlogPages.Should().Be(2);
        summary.ClassicASPXPages.Should().Be(1);
        summary.ClassicPages.Should().Be(3);
        summary.AggregatedRemediationCodes.Split(',').Should().BeEquivalentTo("CP4", "CP5");
        summary.PagesWithWebParts.Should().Be(0, "shared form web parts are not item-level inventories");
        (await read.ClassicAspxInventories.CountAsync(value => value.ScanId == scan)).Should().Be(1);
        var discovery = await read.ClassicPageDiscoveries.Where(row => row.ScanId == scan).ToListAsync();
        discovery.Should().ContainSingle().Which.RecordKey.Should().Be(physical.RecordKey);
        discovery.Should().NotContain(row => row.ScopeType == "ListItem" || row.RowType == "ReferenceBinding" ||
            row.Url.Contains("?ID=", StringComparison.OrdinalIgnoreCase));

        string directory = Path.Combine(Path.GetTempPath(), "legacy-blog-replay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await ReportManager.ExportClassicReportDataAsync(read, scan, directory,
                new CsvConfiguration(CultureInfo.InvariantCulture));
            var report = ReadCsv<ClassicPage>(directory, "classicpages.csv");
            report.Should().HaveCount(3);
            report.Where(page => page.PageType == "BlogPage").Should().HaveCount(2)
                .And.OnlyContain(page => page.RemediationCode == "CP4" && page.FileUniqueId == null);
            report.Select(page => page.PageUrl).Should().BeEquivalentTo(stored.Select(page => page.PageUrl));
            report.Single(page => page.PageUrl.EndsWith("Special.aspx?ID=7", StringComparison.Ordinal)).PageName
                .Should().Be(stored.Single(page => page.ListId == posts.Id && page.ListItemId == 7).PageName);
            ReadCsv<ClassicWebSummary>(directory, "classicwebsummaries.csv").Should().ContainSingle()
                .Which.ClassicBlogPages.Should().Be(2);
            ReadCsv<ClassicPageDiscovery>(directory, "discovery.csv").Should().ContainSingle()
                .Which.Url.Should().Be(physical.Url);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Leaves HOME's already selected physical target untouched and makes no legacy item-stream calls.</summary>
    [Fact]
    public async Task Home_only_never_enumerates_eligible_post_items_or_adds_assessment_rows()
    {
        var posts = Posts();
        var pages = await AssessAsync(Guid.NewGuid(), new[] { posts }, homePageOnly: true);
        pages.Should().BeEmpty();
        posts.Requests.Should().BeEmpty();
        posts.MetadataReads.Should().Be(0, "HOME should return before consulting any list metadata");
    }

    /// <summary>Rejects list-wide and item-specific stock forms, including the stock-name customization limitation.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Lists/Tasks/DispForm.aspx")]
    [InlineData("Lists/Tasks/DISPFORM.ASPX?source=test")]
    [InlineData("~layouts/WrkTaskIP.aspx")]
    [InlineData("~sitecollectionlayouts/userdisp.aspx")]
    [InlineData("~unrecognized/Page.aspx")]
    public async Task Ineligible_lists_do_not_read_items(string form)
    {
        var list = new ListFixture(ListBaseType.GenericList, ListTemplateType.GenericList, "Tasks", form,
            Array.Empty<(string, string)>(), new[] { Item(1, "0x01", "ordinary row") });
        (await AssessAsync(Guid.NewGuid(), new[] { list })).Should().BeEmpty();
        list.Requests.Should().BeEmpty();
    }

    /// <summary>Honors longest CT overrides, stock-item exclusion, empty overrides and the list fallback per item.</summary>
    [Fact]
    public async Task Mixed_content_types_keep_stock_siblings_out_and_use_most_specific_nonempty_override()
    {
        var list = new ListFixture(ListBaseType.GenericList, ListTemplateType.GenericList, "Mixed",
            "~site/Lists/Mixed/Fallback.aspx", new[]
            {
                ("0x01", "~site/Lists/Mixed/Base.aspx"),
                ("0x0102", "~site/Lists/Mixed/DispForm.aspx"),
                ("0x0101", "~sitecollection/Lists/Mixed/Specific.aspx"),
                ("0x010100", ""),
            }, new[]
            {
                Item(1, "0X010100AA", "specific"),
                Item(2, "0x0102AA", "stock sibling"),
                Item(3, "0x02", "fallback"),
            });
        var pages = await AssessAsync(Guid.NewGuid(), new[] { list });
        pages.Select(page => page.PageUrl).Should().BeEquivalentTo(
            "/sites/reuse/Lists/Mixed/Specific.aspx?ID=1", Web + "/Lists/Mixed/Fallback.aspx?ID=3");
        pages.Should().OnlyContain(page => page.PageType == "ASPXPage" && page.RemediationCode == "CP5");
    }

    /// <summary>Preserves the original report classification: a Post CT alone does not turn a custom non-Posts list into CP4.</summary>
    [Fact]
    public async Task Non_posts_list_with_post_content_type_remains_custom_aspx_assessment()
    {
        var list = new ListFixture(ListBaseType.GenericList, ListTemplateType.GenericList, "Custom",
            StockForm, new[] { (PostType, PostForm) }, new[] { Item(1, PostType + "00AA", "custom") });
        var page = (await AssessAsync(Guid.NewGuid(), new[] { list })).Should().ContainSingle().Which;
        page.PageType.Should().Be("ASPXPage");
        page.RemediationCode.Should().Be("CP5");
    }

    /// <summary>Propagates cancellation before metadata or between item-stream pages without fabricating partial success.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_before_or_during_stream_does_not_read_another_page(bool duringStream)
    {
        var posts = Posts();
        using var cancel = new CancellationTokenSource();
        if (duringStream)
        {
            posts.AfterRead = cancel.Cancel;
        }
        else
        {
            cancel.Cancel();
        }

        var run = () => AssessAsync(Guid.NewGuid(), new[] { posts }, cancellationToken: cancel.Token);
        await run.Should().ThrowAsync<OperationCanceledException>();
        posts.Requests.Should().HaveCount(duringStream ? 1 : 0);
    }

    /// <summary>Creates only the legacy orchestration inputs; all page rows/codes come from the production branch.</summary>
    private static PageScanComponent.PageDiscovery NewAssessment(Guid scan, bool skipUserInformation)
    {
        return new PageScanComponent.PageDiscovery
        {
            ScannerBase = new ClassicScanner(null, null, null, scan, Site, Web, "BLOG#0", new ClassicOptions()),
            SkipUserInformation = skipUserInformation,
            Pages = new List<ClassicPage>(),
            RemediationCodes = new HashSet<string>(),
            EnrichmentInputs = new List<PageScanComponent.PageEnrichmentInput>(),
        };
    }

    /// <summary>Passes seeded SDK lists to the production assessment without constructing any report rows.</summary>
    private static Task<List<ClassicPage>> AssessAsync(Guid scan, IReadOnlyList<ListFixture> lists,
        bool skipUserInformation = true, bool homePageOnly = false, CancellationToken cancellationToken = default)
    {
        return LegacyDisplayFormAssessment.ReadAsync(scan, Site, Web, new Uri(WebAddress),
            lists.Select(value => value.List).ToArray(), homePageOnly, skipUserInformation, cancellationToken);
    }

    /// <summary>Seeds a Posts list with parent/specialized forms and a stock-form sibling across two native pages.</summary>
    private static ListFixture Posts()
    {
        return new ListFixture(ListBaseType.GenericList, ListTemplateType.Posts, "Posts", StockForm,
            new[] { (PostType, PostForm), (SpecializedPostType, SpecializedPostForm) },
            new[] { Item(7, SpecializedPostType + "8A", QuotedPostTitle) },
            new[] { Item(3, PostType + "00AA", "Older post"), Item(9, "0x01", "ordinary row") });
    }

    /// <summary>Seeds item field data only; any attempt to access a File API fails in the strict proxy.</summary>
    private static IListItem Item(int id, string contentTypeId, string title)
    {
        var values = (TransientDictionary)Activator.CreateInstance(typeof(TransientDictionary), nonPublic: true);
        values.Add("ContentTypeId", contentTypeId);
        values.Add("Title", title);
        values.Add("Modified", ModifiedAt);
        values.Add("Editor", StrictProxy.Create<IFieldUserValue>((method, _) => method.Name switch
        {
            "get_Email" => "author@contoso.com",
            "get_LookupValue" => "Author",
            _ => throw new InvalidOperationException("Unexpected user call: " + method.Name),
        }));
        return StrictProxy.Create<IListItem>((method, _) => method.Name switch
        {
            "get_Id" => id,
            "get_Values" => values,
            _ => throw new InvalidOperationException("Unexpected item/file metadata call: " + method.Name),
        });
    }

    /// <summary>Seeds one independent physical form observation without assigning its identity to any item address.</summary>
    private static ClassicPageDiscovery PhysicalForm(Guid scan, Guid list)
    {
        return new ClassicPageDiscovery
        {
            ScanId = scan,
            SiteUrl = Site,
            WebUrl = Web,
            RecordKey = "page:physical-form",
            RowType = "Page",
            ScopeType = "File",
            Url = Web + "/Lists/Posts/Post.aspx",
            FileName = "Post.aspx",
            SiteCollectionId = Guid.NewGuid(),
            WebId = Guid.NewGuid(),
            ListId = list,
            FileUniqueId = Guid.NewGuid(),
            DiscoveryStatus = "Discovered",
            ObservedAtUtc = ModifiedAt,
        };
    }

    /// <summary>Reads the product's native CSV output back through its real typed schema.</summary>
    private static T[] ReadCsv<T>(string directory, string name)
    {
        using var csv = new CsvReader(new StreamReader(Path.Combine(directory, name)), CultureInfo.InvariantCulture);
        return csv.GetRecords<T>().ToArray();
    }

    /// <summary>Models the declared list/CT metadata and paged native item stream, rejecting all unplanned SDK access.</summary>
    private sealed class ListFixture
    {
        internal Guid Id { get; } = Guid.NewGuid();
        internal IList List { get; }
        internal List<RenderListDataOptions> Requests { get; } = new();
        internal int MetadataReads { get; private set; }
        internal Action AfterRead { get; set; }

        /// <summary>Creates strict SDK proxies that enforce recursive paged CAML, cache clearing and no File metadata reads.</summary>
        internal ListFixture(ListBaseType baseType, ListTemplateType template, string title, string defaultForm,
            IReadOnlyList<(string Id, string Form)> forms, params IListItem[][] pages)
        {
            var contentTypes = forms.Select(form => StrictProxy.Create<IContentType>((method, _) => method.Name switch
            {
                "get_StringId" => form.Id,
                "get_DisplayFormUrl" => form.Form,
                _ => throw new InvalidOperationException("Unexpected content-type call: " + method.Name),
            })).ToList();
            var contentTypeCollection = StrictProxy.Create<IContentTypeCollection>((method, _) => method.Name == "get_RequestedItems"
                ? contentTypes
                : throw new InvalidOperationException("Unexpected CT collection call: " + method.Name));
            var requested = new List<IListItem> { Item(999, PostType, "Stale cached item must be cleared") };
            var items = StrictProxy.Create<IListItemCollection>((method, _) =>
            {
                if (method.Name == "Clear")
                {
                    requested.Clear();
                    return null;
                }
                if (method.Name == "get_RequestedItems")
                {
                    return requested;
                }
                throw new InvalidOperationException("Unexpected item collection call: " + method.Name);
            });
            var folder = StrictProxy.Create<IFolder>((method, _) => method.Name == "get_ServerRelativeUrl"
                ? Web + "/Lists/" + title
                : throw new InvalidOperationException("Unexpected folder/file call: " + method.Name));
            List = StrictProxy.Create<IList>((method, args) =>
            {
                if (method.Name == nameof(IList.LoadListDataAsStreamAsync))
                {
                    var options = (RenderListDataOptions)args[0];
                    int index = Requests.Count;
                    index.Should().BeLessThan(pages.Length);
                    options.RenderOptions.Should().Be(RenderListDataOptionsFlags.ListData);
                    var view = XDocument.Parse(options.ViewXml).Root;
                    view.Attribute("Scope").Value.Should().Be("RecursiveAll");
                    view.Element("Query").Should().BeNull("legacy rows have no ASPX-file filter");
                    view.Element("RowLimit").Value.Should().Be("1000");
                    view.Element("RowLimit").Attribute("Paged").Value.Should().Be("TRUE");
                    view.Element("OrderBy").Element("FieldRef").Attribute("Name").Value.Should().Be("ID");
                    requested.Should().BeEmpty("cached stream rows must be cleared before every page");
                    requested.AddRange(pages[index]);
                    Requests.Add(options);
                    AfterRead?.Invoke();
                    return Task.FromResult(index + 1 < pages.Length
                        ? new Dictionary<string, object> { ["NextHref"] = "?Paged=TRUE&p_ID=7" }
                        : new Dictionary<string, object>());
                }

                MetadataReads++;
                return method.Name switch
                {
                    "get_Id" => Id,
                    "get_BaseType" => baseType,
                    "get_TemplateType" => template,
                    "get_Title" => title,
                    "get_DefaultDisplayFormUrl" => defaultForm,
                    "get_ContentTypes" => contentTypeCollection,
                    "get_Items" => items,
                    "get_RootFolder" => folder,
                    _ => throw new InvalidOperationException("Unexpected list/file call: " + method.Name),
                };
            });
        }
    }

    /// <summary>Dispatches only explicitly supported SDK members and fails on accidental network/file APIs.</summary>
    public class StrictProxy : DispatchProxy
    {
        private Func<MethodInfo, object[], object> invoke;

        /// <summary>Creates one interface proxy with an explicit invocation contract.</summary>
        internal static T Create<T>(Func<MethodInfo, object[], object> invoke) where T : class
        {
            var instance = Create<T, StrictProxy>();
            ((StrictProxy)(object)instance).invoke = invoke;
            return instance;
        }

        /// <summary>Forwards each SDK member to the strict fixture contract.</summary>
        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            return invoke(targetMethod, args);
        }
    }
}
