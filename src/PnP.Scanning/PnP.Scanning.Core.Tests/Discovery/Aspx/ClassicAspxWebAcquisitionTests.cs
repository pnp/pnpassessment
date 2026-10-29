using FluentAssertions;
using PnP.Scanning.Core.Discovery.Aspx;
using PnP.Scanning.Core.Storage;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery.Aspx;

public sealed class ClassicAspxWebAcquisitionTests
{
    /// <summary>Copies the actual folder-file identity and metadata without a SharePoint client or source-content read.</summary>
    [Fact]
    public async Task ProjectsObservedFolderFilesWithoutAdditionalAcquisition()
    {
        var scanId = Guid.NewGuid();
        var source = ClassicAspxStreamingFixture.Page(scanId, 1);
        source.ListId = Guid.NewGuid();
        source.FolderUniqueId = Guid.NewGuid();
        source.ContentTypeId = "0x0101";
        var inventories = new List<ClassicAspxInventory>();
        await new ClassicAspxWebAcquisition(ClassicAspxStreamingFixture.Context(scanId)).AcquireAsync(
            Rows(source), (batch, _) => { inventories.AddRange(batch.Inventory); return Task.CompletedTask; });
        inventories.Should().ContainSingle();
        var row = inventories.Single();
        row.AspxKey.Should().Be(ClassicAspxInventory.CreateTenantFileKey(source.SiteCollectionId.Value, source.FileUniqueId.Value));
        row.ListId.Should().Be(source.ListId);
        row.FolderUniqueId.Should().Be(source.FolderUniqueId);
        row.ContentTypeId.Should().Be("0x0101");
        row.ProvenanceJson.Should().Contain("Folder.Files");
    }

    /// <summary>Rejects missing/foreign identity and non-ASPX locators as explicit gaps instead of inventing a file GUID.</summary>
    [Theory]
    [InlineData("scan")]
    [InlineData("site")]
    [InlineData("file")]
    [InlineData("suffix")]
    [InlineData("authority")]
    public async Task UnprovedPhysicalFactsRemainGaps(string invalid)
    {
        var scanId = Guid.NewGuid();
        var row = ClassicAspxStreamingFixture.Page(scanId, 1);
        switch (invalid)
        {
            case "scan": row.ScanId = Guid.NewGuid(); break;
            case "site": row.SiteCollectionId = Guid.NewGuid(); break;
            case "file": row.FileUniqueId = null; break;
            case "suffix": row.Url = ClassicAspxStreamingFixture.SiteUrl + "/file.txt"; break;
            case "authority": row.Url = "https://other.example/file.aspx"; break;
        }
        var batches = new List<ClassicAspxAcquisitionBatch>();
        await new ClassicAspxWebAcquisition(ClassicAspxStreamingFixture.Context(scanId)).AcquireAsync(
            Rows(row), (batch, _) => { batches.Add(batch); return Task.CompletedTask; });
        batches.SelectMany(batch => batch.Inventory).Should().BeEmpty();
        batches.SelectMany(batch => batch.Evidence).Should().ContainSingle()
            .Which.ErrorCodes.Should().Be("physical_identity_or_scope_unproved");
    }

    /// <summary>A metadata declaration with a valid-looking URL or GUID cannot become a physical inventory observation.</summary>
    [Fact]
    public async Task ReferenceDeclarationsCannotCreateInventory()
    {
        var scanId = Guid.NewGuid();
        var row = ClassicAspxStreamingFixture.Page(scanId, 1);
        row.RowType = "Reference";
        int publications = 0;
        await new ClassicAspxWebAcquisition(ClassicAspxStreamingFixture.Context(scanId)).AcquireAsync(
            Rows(row), (_, _) => { publications++; return Task.CompletedTask; });
        publications.Should().Be(0);
    }

    /// <summary>Leaves unproved owner scopes nullable rather than substituting the Web from which a file was observed.</summary>
    [Fact]
    public async Task DoesNotGuessMissingOwnerScopes()
    {
        var scanId = Guid.NewGuid();
        var row = ClassicAspxStreamingFixture.Page(scanId, 1);
        row.WebId = null;
        var inventories = new List<ClassicAspxInventory>();
        await new ClassicAspxWebAcquisition(ClassicAspxStreamingFixture.Context(scanId)).AcquireAsync(
            Rows(row), (batch, _) => { inventories.AddRange(batch.Inventory); return Task.CompletedTask; });
        var result = inventories.Single();
        result.WebId.Should().BeNull();
        result.ListId.Should().BeNull();
        result.FolderUniqueId.Should().BeNull();
    }

    /// <summary>Propagates caller cancellation before publishing any success batch.</summary>
    [Fact]
    public async Task CallerCancellationIsNotConvertedToEmptySuccess()
    {
        var scanId = Guid.NewGuid();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var run = () => new ClassicAspxWebAcquisition(ClassicAspxStreamingFixture.Context(scanId)).AcquireAsync(
            ClassicAspxStreamingFixture.Pages(scanId, 5), (_, _) => Task.CompletedTask, cancel.Token);
        await run.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>Accepts the production physical-file state without converting unknown observations into complete coverage.</summary>
    [Theory]
    [InlineData("Discovered", "Observed")]
    [InlineData("Complete", "Observed")]
    [InlineData("Unknown", "Unknown")]
    public async Task PhysicalObservationStateIsNotTenantCompleteness(string state, string expected)
    {
        var scanId = Guid.NewGuid();
        var physical = ClassicAspxStreamingFixture.Page(scanId, 1);
        physical.DiscoveryStatus = state;
        var inventory = new List<ClassicAspxInventory>();
        await new ClassicAspxWebAcquisition(ClassicAspxStreamingFixture.Context(scanId)).AcquireAsync(
            Rows(physical), (batch, _) => { inventory.AddRange(batch.Inventory); return Task.CompletedTask; });
        inventory.Single().CoverageState.Should().Be(expected);
    }

    /// <summary>Retains GUID-backed physical files with decoded or encoded hash characters in folder and file ResourcePaths.</summary>
    [Theory]
    [InlineData("/sites/inventory/Docs/A#B/page.aspx")]
    [InlineData("https://example.sharepoint.com/sites/inventory/Docs/A#B/page.aspx")]
    [InlineData("/sites/inventory/Docs/A%23B/page.aspx")]
    public async Task PhysicalHashNamesAreNotReferenceFragments(string locator)
    {
        var scanId = Guid.NewGuid();
        var physical = ClassicAspxStreamingFixture.Page(scanId, 1);
        physical.Url = locator;
        var inventory = new List<ClassicAspxInventory>();
        var gaps = new List<ClassicPageDiscovery>();
        await new ClassicAspxWebAcquisition(ClassicAspxStreamingFixture.Context(scanId)).AcquireAsync(
            Rows(physical), (batch, _) =>
            {
                inventory.AddRange(batch.Inventory);
                gaps.AddRange(batch.Evidence);
                return Task.CompletedTask;
            });
        gaps.Should().BeEmpty();
        inventory.Single().Url.Should().Contain("A%23B/page.aspx");
        inventory.Single().FileUniqueId.Should().Be(physical.FileUniqueId.Value);
    }

    /// <summary>Exposes a tiny asynchronous physical observation stream without building a fake REST item endpoint.</summary>
    private static async IAsyncEnumerable<ClassicPageDiscovery> Rows(ClassicPageDiscovery row)
    {
        yield return row;
        await Task.CompletedTask;
    }
}
