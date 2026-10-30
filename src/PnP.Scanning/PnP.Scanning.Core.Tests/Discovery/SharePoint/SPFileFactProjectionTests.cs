using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Storage;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery;

/// <summary>Proves raw physical facts are independent of file extension and business classification.</summary>
public sealed class SPFileFactProjectionTests
{
    private static readonly Guid SiteId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid WebId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>Preserves native decoded percent/hash names and non-ASPX file observations without a synthetic key.</summary>
    [Theory]
    [InlineData("A%20B.aspx")]
    [InlineData("A%2fB.aspx")]
    [InlineData("A#B.aspx")]
    [InlineData("default.master")]
    [InlineData("document.docx")]
    public void Physical_facts_preserve_every_native_file_type_and_decoded_locator(string name)
    {
        var scanId = Guid.NewGuid();
        var record = Record(1, name);
        var result = SPFileFactProjection.FromPhysicalRecord(scanId, record);
        result.Should().NotBeNull();
        result.ScanId.Should().Be(scanId);
        result.SiteCollectionId.Should().Be(SiteId);
        result.FileUniqueId.Should().Be(Guid.Parse(record.FileUniqueId));
        result.ServerRelativeUrl.Should().Be("/sites/a/Docs/" + name);
        result.FileName.Should().Be(name);
        result.NoExecuteRaw.Should().BeNull();
        result.NoExecuteReadState.Should().Be("NotRequested");
    }

    /// <summary>Does not turn metadata declarations or missing file GUIDs into keyed physical resources.</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Unproved_physical_records_do_not_create_facts(bool guaranteed, bool fileId)
    {
        var record = Record(1, "page.aspx") with
        {
            LocatorIsGuaranteedPhysicalFilePath = guaranteed,
            FileUniqueId = fileId ? Guid.NewGuid().ToString("D") : null
        };
        SPFileFactProjection.FromPhysicalRecord(Guid.NewGuid(), record).Should().BeNull();
    }

    /// <summary>Records existing heuristic inputs and exact field presence while keeping Wiki content out of persistence.</summary>
    [Fact]
    public void Item_metadata_captures_raw_inputs_without_running_classification()
    {
        var row = Page(Guid.NewGuid());
        var values = new Dictionary<string, object>
        {
            ["ClientSideApplicationId"] = "B6917CB1-93A0-4B97-A84D-7CF49975D4EC",
            ["HTML_x0020_File_x0020_Type"] = null,
            ["WikiField"] = "sensitive wiki content",
            ["BSN"] = string.Empty
        };
        var result = SPFileFactProjection.FromItemMetadata(row, values);
        result.ItemMetadataReadState.Should().Be("Available");
        result.ClientSideApplicationId.Should().Be((string)values["ClientSideApplicationId"]);
        result.WikiFieldHasValue.Should().BeTrue();
        result.ItemMetadataFieldStatesJson.Should().Contain("omitted").And.Contain("null").And.Contain("value");
        result.ProvenanceJson.Should().NotContain("sensitive wiki content");
        result.CoverageState.Should().Be(ClassicAspxCoverageStates.Observed);
    }

    /// <summary>Copies the actual string returned for the requested NoExecute field verbatim; decoding is exclusively an export concern.</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("01")]
    [InlineData(" 1 ")]
    [InlineData("")]
    public void NoExecute_original_field_string_is_not_normalized_during_capture(string raw)
    {
        var result = SPFileFactProjection.FromItemMetadata(Page(Guid.NewGuid()),
            new Dictionary<string, object> { ["NoExecute"] = raw });
        result.NoExecuteRaw.Should().Be(raw);
        result.NoExecuteSource.Should().Be("SPO.ListItem.NoExecute");
        result.NoExecuteReadState.Should().Be("Available");
        result.ItemMetadataFieldStatesJson.Should().Contain("NoExecute");
    }

    /// <summary>Separates an omitted NoExecute field, explicit NULL and an unexpected runtime type without inventing a false value.</summary>
    [Theory]
    [InlineData("omitted", "NotReturned")]
    [InlineData("null", "Null")]
    [InlineData("wrong-type", "InvalidType")]
    public void NoExecute_unknown_observations_never_become_zero(string input, string expected)
    {
        var values = new Dictionary<string, object>();
        if (input != "omitted")
        {
            values["NoExecute"] = input == "null" ? null : true;
        }
        var result = SPFileFactProjection.FromItemMetadata(Page(Guid.NewGuid()), values);
        result.NoExecuteReadState.Should().Be(expected);
        result.NoExecuteRaw.Should().NotBe("0");
        if (input is "omitted" or "null")
        {
            result.NoExecuteRaw.Should().BeNull();
        }
    }

    /// <summary>Commits on-demand file facts in bounded chunks and converges after a complete replay.</summary>
    [Fact]
    public async Task Ten_thousand_mixed_physical_files_are_streamed_and_replay_idempotent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ScanContext>().UseSqlite(connection).Options;
        using var context = new ScanContext(options);
        await context.Database.MigrateAsync();
        var scanId = Guid.NewGuid();
        var writer = new AssessmentDiscoveryWriter(() => new ScanContext(options));
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await writer.WritePhysicalFilesAsync(scanId, Records(10000), CancellationToken.None);
        }
        (await context.SPFiles.CountAsync()).Should().Be(10000);
        (await context.SPFiles.CountAsync(file => file.FileName.EndsWith(".master"))).Should().BeGreaterThan(0);
        (await context.ClassicPageDiscoveries.CountAsync()).Should().Be(0, "raw facts alone do not manufacture ASPX assessment rows");
    }

    /// <summary>Produces a deterministic physical stream without materializing a complete file/item universe in the fixture.</summary>
    private static IEnumerable<RawDiscoveryRecord> Records(int count)
    {
        for (int ordinal = 1; ordinal <= count; ordinal++)
        {
            yield return Record(ordinal, ordinal % 2 == 0 ? ordinal + ".master" : ordinal + ".aspx");
        }
    }

    /// <summary>Builds a server-observed synthetic file GUID and decoded locator stable across replay.</summary>
    private static RawDiscoveryRecord Record(int ordinal, string name) => new(
        ordinal.ToString(), new Guid(ordinal, 0, 0, new byte[8]).ToString("D"), "folder", name,
        "/sites/a/Docs/" + name, true, "fixture", SiteCollectionId: SiteId, WebId: WebId,
        SiteUrl: "https://example.sharepoint.com/sites/a", WebUrl: "https://example.sharepoint.com/sites/a");

    /// <summary>Creates a physical discovery row whose stable identity predates optional item-metadata enrichment.</summary>
    private static ClassicPageDiscovery Page(Guid scanId) => new()
    {
        ScanId = scanId,
        SiteCollectionId = SiteId,
        FileUniqueId = Guid.NewGuid(),
        WebId = WebId,
        RowType = "Page",
        ScopeType = "File",
        RecordKey = "fixture:page",
        Url = "/sites/a/Docs/page.aspx"
    };
}
