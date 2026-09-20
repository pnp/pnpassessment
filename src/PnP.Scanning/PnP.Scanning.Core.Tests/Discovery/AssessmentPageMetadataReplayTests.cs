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
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Xml.Linq;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery;

/// <summary>
/// Offline replay of the native scan's list-item projection regression. No credentials, network,
/// daemon or tenant are used. The SDK boundary is a strict test double; metadata loading/projection,
/// discovery persistence, EF migrations and native CSV export are production code.
/// This does not establish live SharePoint coverage or validate the SDK's HTTP serialization.
/// </summary>
[Trait("Category", "ClassicDiscoveryIntegration")]
public sealed class AssessmentPageMetadataReplayTests : IClassFixture<ScanContextFixture>
{
    private const string Site = "https://contoso.sharepoint.com/sites/replay";
    private const string Web = "/sites/replay";
    private const string PublishingContentType = "0x010100C568DB52D9D0A14D9B2FDCC96666E9F2007948130EC3DB064584E219954237AF39004C1F8B46085B4D22B1CDC3DE08CFFB9C0";
    private readonly ScanContextFixture database;

    public AssessmentPageMetadataReplayTests(ScanContextFixture database) => this.database = database;

    [Fact]
    public async Task Same_item_number_in_different_libraries_survives_projection_database_and_native_report()
    {
        var scan = Guid.NewGuid();
        var fixtures = new[]
        {
            new MetadataFixture(scan, "Pages/publishing.aspx", new()
            {
                ["ContentTypeId"] = PublishingContentType,
                ["Title"] = "Publishing, \"replay\"\npage",
                ["FileRef"] = Web + "/Pages/publishing.aspx",
            }),
            new MetadataFixture(scan, "WikiPages/wiki.aspx", new() { ["WikiField"] = "<p>Wiki body</p>" }),
            new MetadataFixture(scan, "Docs/webpart.aspx", new() { ["HTML_x0020_File_x0020_Type"] = "SharePoint.WebPartPage.Document" }),
            new MetadataFixture(scan, "Custom/raw.aspx", new() { ["BSN"] = "custom" }),
            new MetadataFixture(scan, "SitePages/modern.aspx", new()
            {
                ["ClientSideApplicationId"] = Guid.Parse("B6917CB1-93A0-4B97-A84D-7CF49975D4EC"),
            }),
        };
        var writer = new AssessmentDiscoveryWriter(database.CreateContext);
        await writer.WriteAsync(fixtures.Select(fixture => fixture.Row));
        var assessedPages = new List<ClassicPage>();

        foreach (var fixture in fixtures)
        {
            var input = await PageScanComponent.LoadPhysicalPageAsync(fixture.List, fixture.Row, null, true);
            fixture.StreamRequests.Should().Be(1, "computed metadata requires explicit native list-stream ViewFields");
            fixture.AllProjectionRequests.Should().Be(0, "$select=* is not complete page metadata");
            fixture.Row.PageType = input.Page.PageType;
            fixture.Row.AssessmentStatus = input.Page.AddToDatabase() ? "Complete" : "NotApplicable";
            if (input.Page.PageType == PageScanComponent.WikiPage)
            {
                input.WikiFieldHtml.Should().Be("<p>Wiki body</p>");
                // Replay a later WP-extraction failure. Physical discovery must not disappear.
                fixture.Row.AssessmentStatus = "Failed";
                AssessmentWebDiscovery.AddError(fixture.Row, "WebPartAssessment", "HTTP403", "access denied, \"web parts\"\nretained page");
            }
            PageScanComponent.ApplyDiscoveryState(input.Page, fixture.Row);
            if (input.Page.AddToDatabase()) assessedPages.Add(input.Page);
            await writer.WriteAsync(new[] { fixture.Row });
        }
        var denied = new ClassicPageDiscovery
        {
            ScanId = scan, SiteUrl = Site, WebUrl = Web, RecordKey = "scope:restricted-list",
            RowType = "Scope", ScopeType = "Container", Url = Web + "/Restricted",
            DiscoveryStatus = "Denied", ErrorStage = "ListEnumeration", ErrorCodes = "HTTP403",
            ErrorDetail = "403 while enumerating list; not a fabricated page", ObservedAtUtc = DateTime.UtcNow,
        };
        await writer.WriteAsync(new[] { denied });

        using (var db = database.CreateContext())
        {
            // The original failure occurred here: each PageUrl was the string "1", so EF rejected
            // tracking the second page. All five fixtures intentionally share that list-item number.
            db.ClassicPages.AddRange(assessedPages);
            await db.SaveChangesAsync();
        }
        using var read = database.CreateContext();
        var stored = await read.ClassicPages.Where(page => page.ScanId == scan).ToListAsync();
        stored.Should().HaveCount(4);
        stored.Select(page => page.PageUrl).Should().OnlyHaveUniqueItems();
        stored.Should().OnlyContain(page => page.ListItemId == 1 && page.PageUrl.StartsWith(Web + "/"));
        stored.Should().OnlyContain(page => page.FileUniqueId != null && page.SiteCollectionId != null && page.WebId != null);
        stored.Should().OnlyContain(page => page.HomePage == null && page.ModifiedBy == null);
        stored.Select(page => page.PageType).Should().BeEquivalentTo("PublishingPage", "WikiPage", "WebPartPage", "ASPXPage");
        stored.Single(page => page.PageType == "WikiPage").AssessmentStatus.Should().Be("Failed");

        var reportDirectory = Path.Combine(Path.GetTempPath(), "assessment-native-metadata-replay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(reportDirectory);
        try
        {
            await ReportManager.ExportClassicReportDataAsync(read, scan, reportDirectory, new CsvConfiguration(CultureInfo.InvariantCulture));
            var classic = ReadCsv<ClassicPage>(reportDirectory, "classicpages.csv");
            var discovery = ReadCsv<ClassicPageDiscovery>(reportDirectory, "discovery.csv");
            classic.Should().HaveCount(4);
            classic.Single(page => page.PageType == "PublishingPage").PageName.Should().Be("Publishing, \"replay\"\npage");
            classic.Single(page => page.PageType == "WikiPage").AssessmentStatus.Should().Be("Failed");
            classic.Select(page => page.FileUniqueId).Should().BeEquivalentTo(stored.Select(page => page.FileUniqueId));
            discovery.Where(row => row.RowType == "Page").Should().HaveCount(5);
            discovery.Where(row => row.RowType == "Page").Should().OnlyContain(row => row.DiscoveryStatus == "Discovered");
            discovery.Single(row => row.PageType == "ModernPage").AssessmentStatus.Should().Be("NotApplicable");
            discovery.Single(row => row.PageType == "WikiPage").ErrorDetail.Should().Contain("access denied, \"web parts\"\nretained page");
            discovery.Single(row => row.RowType == "Scope").FileUniqueId.Should().BeNull();
            discovery.Should().HaveCount(6);
            Directory.GetFiles(reportDirectory, "*gap*.csv").Should().BeEmpty();
        }
        finally { Directory.Delete(reportDirectory, recursive: true); }
    }

    [Fact]
    public async Task Wildcard_rest_projection_omits_computed_fields_but_native_list_stream_loads_them()
    {
        var fixture = new MetadataFixture(Guid.NewGuid(), "Pages/webpart.aspx", new()
        {
            ["ContentTypeId"] = "0x010109006DC758714F143A4FB8A7253506F98C5F",
            ["HTML_x0020_File_x0020_Type"] = "SharePoint.WebPartPage.Document",
        });
        var wildcardItem = await fixture.List.Items.GetByIdAsync(1, value => value.All);
        wildcardItem.Values.Should().ContainKey("ContentTypeId");
        wildcardItem.Values.Should().NotContainKey("HTML_x0020_File_x0020_Type");
        wildcardItem.Values.Should().NotContainKey("FileLeafRef");
        var loaded = await PageScanComponent.LoadPhysicalPageAsync(fixture.List, fixture.Row, null, true);
        loaded.Page.PageType.Should().Be("WebPartPage");
        loaded.Page.PageUrl.Should().Be(fixture.Row.Url);
        loaded.FileLeafRef.Should().Be("webpart.aspx");
        fixture.AllProjectionRequests.Should().Be(1);
        fixture.StreamRequests.Should().Be(1);
        fixture.LastViewFields.Should().Contain("ClientSideApplicationId", "an absent optional modern field is allowed in the native CAML query");
        fixture.LastViewFields.Should().NotContain("Editor", "SkipUserInformation should avoid requesting user details");
    }

    [Fact]
    public async Task Native_list_stream_modern_marker_is_classified_without_inflating_classic_rows()
    {
        var fixture = new MetadataFixture(Guid.NewGuid(), "SitePages/modern.aspx", new()
        {
            ["HTML_x0020_File_x0020_Type"] = "",
            ["ClientSideApplicationId"] = "{B6917CB1-93A0-4B97-A84D-7CF49975D4EC}",
            ["BSN"] = "265",
        });
        var loaded = await PageScanComponent.LoadPhysicalPageAsync(fixture.List, fixture.Row, null, true);
        loaded.Page.PageType.Should().Be("ModernPage");
        loaded.Page.AddToDatabase().Should().BeFalse();
        fixture.StreamRequests.Should().Be(1);
    }

    [Fact]
    public async Task Native_metadata_query_requests_editor_only_when_user_information_is_enabled()
    {
        var fixture = new MetadataFixture(Guid.NewGuid(), "Pages/wiki.aspx", new() { ["WikiField"] = "<p>body</p>" });
        await PageScanComponent.LoadPhysicalPageAsync(fixture.List, fixture.Row, null, false);
        fixture.LastViewFields.Should().Contain("Editor");
    }

    [Fact]
    public async Task Publishing_type_can_fall_back_to_discovered_content_type()
    {
        var fixture = new MetadataFixture(Guid.NewGuid(), "Pages/enterprise.aspx", new());
        fixture.Row.ContentTypeId = PublishingContentType;
        var loaded = await PageScanComponent.LoadPhysicalPageAsync(fixture.List, fixture.Row, null, true);
        loaded.Page.PageType.Should().Be("PublishingPage");
        loaded.Page.PageName.Should().Be("enterprise");
    }

    [Fact]
    public async Task Metadata_for_another_file_is_rejected_without_deleting_its_discovery()
    {
        var fixture = new MetadataFixture(Guid.NewGuid(), "Pages/one.aspx", new() { ["FileRef"] = Web + "/Other/two.aspx" });
        var writer = new AssessmentDiscoveryWriter(database.CreateContext);
        await writer.WriteAsync(new[] { fixture.Row });
        var load = () => PageScanComponent.LoadPhysicalPageAsync(fixture.List, fixture.Row, null, true);
        await load.Should().ThrowAsync<InvalidDataException>().WithMessage("*FileRef differs*");
        (await writer.ReadPagesAsync(fixture.Row.ScanId, Site, Web)).Should().ContainSingle()
            .Which.DiscoveryStatus.Should().Be("Discovered");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(2)]
    public async Task Absent_or_different_item_cannot_be_assessed_as_the_requested_file(int? returnedId)
    {
        var fixture = new MetadataFixture(Guid.NewGuid(), "Pages/one.aspx", new()) { ReturnedId = returnedId };
        var load = () => PageScanComponent.LoadPhysicalPageAsync(fixture.List, fixture.Row, null, true);
        await load.Should().ThrowAsync<InvalidDataException>().WithMessage("*not returned*");
    }

    [Fact]
    public async Task Wrong_list_is_rejected_before_requesting_an_item()
    {
        var fixture = new MetadataFixture(Guid.NewGuid(), "Pages/one.aspx", new());
        fixture.Row.ListId = Guid.NewGuid();
        var load = () => PageScanComponent.LoadPhysicalPageAsync(fixture.List, fixture.Row, null, true);
        await load.Should().ThrowAsync<InvalidDataException>().WithMessage("*list and list-item identity*");
        fixture.ItemRequests.Should().Be(0);
    }

    private static T[] ReadCsv<T>(string directory, string name)
    {
        using var csv = new CsvReader(new StreamReader(Path.Combine(directory, name)), CultureInfo.InvariantCulture);
        return csv.GetRecords<T>().ToArray();
    }

    private sealed class MetadataFixture
    {
        public ClassicPageDiscovery Row { get; }
        public IList List { get; }
        public int? ReturnedId { get; init; } = 1;
        public int ItemRequests { get; private set; }
        public int AllProjectionRequests { get; private set; }
        public int StreamRequests { get; private set; }
        public string[] LastViewFields { get; private set; } = Array.Empty<string>();

        public MetadataFixture(Guid scan, string path, Dictionary<string, object> fields)
        {
            var listId = Guid.NewGuid();
            var fileId = Guid.NewGuid();
            Row = new ClassicPageDiscovery
            {
                ScanId = scan, SiteUrl = Site, WebUrl = Web, RecordKey = "page:" + fileId,
                RowType = "Page", ScopeType = "File", Url = Web + "/" + path,
                SiteCollectionId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                WebId = Guid.Parse("22222222-2222-2222-2222-222222222222"), ListId = listId,
                FileUniqueId = fileId, ListItemId = 1, FileName = Path.GetFileName(path),
                DiscoveryStatus = "Discovered", AssessmentStatus = "Pending", ObservedAtUtc = DateTime.UtcNow,
            };
            fields["FileLeafRef"] = Row.FileName;
            var values = SdkValues(fields);
            var item = StrictProxy.Create<IListItem>((method, _) => method.Name switch
            {
                "get_Id" => ReturnedId.GetValueOrDefault(),
                "get_Values" => values,
                _ => throw new InvalidOperationException("Unexpected item call: " + method.Name),
            });
            var minimalItem = StrictProxy.Create<IListItem>((method, _) => method.Name switch
            {
                "get_Id" => 1,
                "get_Values" => SdkValues(new() { ["Id"] = 1 }),
                _ => throw new InvalidOperationException("Unexpected default-projection call: " + method.Name),
            });
            var computed = new HashSet<string> { "FileRef", "FileLeafRef", "HTML_x0020_File_x0020_Type", "ClientSideApplicationId", "File_x0020_Type", "BSN" };
            var wildcardValues = SdkValues(fields.Where(field => !computed.Contains(field.Key)).ToDictionary(field => field.Key, field => field.Value));
            var wildcardItem = StrictProxy.Create<IListItem>((method, _) => method.Name switch
            {
                "get_Id" => ReturnedId.GetValueOrDefault(),
                "get_Values" => wildcardValues,
                _ => throw new InvalidOperationException("Unexpected wildcard-projection call: " + method.Name),
            });
            var requestedItems = new List<IListItem>();
            var items = StrictProxy.Create<IListItemCollection>((method, args) =>
            {
                if (method.Name == "Clear") { requestedItems.Clear(); return null; }
                if (method.Name == "get_RequestedItems") return requestedItems;
                if (method.Name != nameof(IListItemCollection.GetByIdAsync))
                    throw new InvalidOperationException("Unexpected collection call: " + method.Name);
                ItemRequests++;
                ((int)args[0]).Should().Be(1);
                var selectors = (Expression<Func<IListItem, object>>[])args[1];
                var hasAll = selectors.Any(selector => selector.Body is MemberExpression { Member.Name: nameof(IListItem.All) });
                if (hasAll) AllProjectionRequests++;
                return Task.FromResult(ReturnedId == null ? null : hasAll ? wildcardItem : minimalItem);
            });
            var folder = StrictProxy.Create<IFolder>((method, _) => method.Name == "get_ServerRelativeUrl"
                ? Web + "/" + path.Split('/')[0]
                : throw new InvalidOperationException("Unexpected folder call: " + method.Name));
            List = StrictProxy.Create<IList>((method, args) =>
            {
                if (method.Name == nameof(IList.LoadListDataAsStreamAsync))
                {
                    ItemRequests++;
                    StreamRequests++;
                    var options = (RenderListDataOptions)args[0];
                    options.RenderOptions.Should().Be(RenderListDataOptionsFlags.ListData);
                    var view = XDocument.Parse(options.ViewXml);
                    view.Root.Attribute("Scope").Value.Should().Be("RecursiveAll");
                    var equal = view.Root.Element("Query").Element("Where").Element("Eq");
                    equal.Element("FieldRef").Attribute("Name").Value.Should().Be("ID");
                    equal.Element("Value").Value.Should().Be("1");
                    view.Root.Element("RowLimit").Value.Should().Be("2");
                    LastViewFields = view.Root.Element("ViewFields").Elements("FieldRef").Select(field => field.Attribute("Name").Value).ToArray();
                    LastViewFields.Should().Contain(new[] { "ID", "FileRef", "FileLeafRef", "ContentTypeId", "HTML_x0020_File_x0020_Type", "ClientSideApplicationId", "WikiField" });
                    requestedItems.Should().BeEmpty("the preceding query's cached items must be cleared");
                    if (ReturnedId != null) requestedItems.Add(item);
                    return Task.FromResult(new Dictionary<string, object>());
                }
                return method.Name switch
                {
                    "get_Id" => listId,
                    "get_Title" => path.Split('/')[0],
                    "get_Items" => items,
                    "get_RootFolder" => folder,
                    _ => throw new InvalidOperationException("Unexpected list call: " + method.Name),
                };
            });
        }
    }

    private static TransientDictionary SdkValues(Dictionary<string, object> fields)
    {
        // IListItem.Values uses this public SDK type with an internal constructor. Instantiate
        // only that dictionary; no PnP context, HTTP client or authentication is constructed.
        var values = (TransientDictionary)Activator.CreateInstance(typeof(TransientDictionary), nonPublic: true);
        foreach (var field in fields) values.Add(field.Key, field.Value);
        return values;
    }

    public class StrictProxy : DispatchProxy
    {
        private Func<MethodInfo, object[], object> invoke;

        public static T Create<T>(Func<MethodInfo, object[], object> invoke) where T : class
        {
            var instance = Create<T, StrictProxy>();
            ((StrictProxy)(object)instance).invoke = invoke;
            return instance;
        }

        protected override object Invoke(MethodInfo targetMethod, object[] args) => invoke(targetMethod, args);
    }
}
