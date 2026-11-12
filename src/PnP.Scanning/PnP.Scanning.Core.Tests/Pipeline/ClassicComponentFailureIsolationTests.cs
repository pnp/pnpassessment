#nullable enable
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.ClassicPages;
using PnP.Scanning.Core.ClassicPages.Collection.Module;
using PnP.Scanning.Core.ClassicPages.Contracts.List;
using PnP.Scanning.Core.ClassicPages.Contracts.Module;
using PnP.Scanning.Core.ClassicPages.Contracts.Page;
using PnP.Scanning.Core.ClassicPages.Contracts.Site;
using PnP.Scanning.Core.ClassicPages.Contracts.Web;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Orchestration;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Storage.Pipeline;
using Xunit;

namespace PnP.Scanning.Core.Tests.Pipeline;

/// <summary>
/// Verifies that a retained-component acquisition failure is recorded per Web instead of terminating the assessment.
/// </summary>
[Trait("Category", "Pipeline")]
public sealed class ClassicComponentFailureIsolationTests
{
    /// <summary>A denied component read leaves an explicit Denied receipt and the Pages snapshot still seals and reports.</summary>
    [Fact]
    public async Task Component_permission_failure_is_recorded_and_page_assessment_completes()
    {
        using StoreCase data = new("component-failure-isolation");
        (ScanStatus status, Guid assessment) = await RunAsync(
            data,
            new UnauthorizedAccessException("Synthetic retained-component access denied"));
        using ScanContext db = data.Store.CreateContext(assessment);
        SourceObservationRow receipt = await db.SourceObservations.SingleAsync(
            row => row.SourceIdentity.StartsWith("ClassicComponents:"));
        Assert.Equal(ScanStatus.Finished, status);
        Assert.Equal(AcquisitionStatus.Denied, receipt.AcquisitionStatus);
        Assert.Contains("Synthetic retained-component access denied", receipt.AcquisitionError);
        Assert.True((await db.SourceSnapshots.SingleAsync()).IsSealed);
        Assert.NotEqual(0, await db.AnalysisReportRows.CountAsync());
    }

    /// <summary>Snapshot-integrity faults are not acquisition failures and still stop the assessment.</summary>
    [Fact]
    public async Task Component_integrity_failure_still_terminates_assessment()
    {
        using StoreCase data = new("component-integrity-failure");
        (ScanStatus status, Guid assessment) = await RunAsync(
            data,
            new SnapshotIntegrityException("Synthetic retained-component integrity fault"));
        using ScanContext db = data.Store.CreateContext(assessment);
        Assert.Equal(ScanStatus.Terminated, status);
        Assert.False(await db.SourceObservations.AnyAsync(
            row => row.SourceIdentity.StartsWith("ClassicComponents:")));
    }

    /// <summary>Runs the recorded Pages + Lists fixture with only the component read replaced by the supplied fault.</summary>
    private static async Task<(ScanStatus Status, Guid Assessment)> RunAsync(StoreCase data, Exception fault)
    {
        ClassicContentFixture recorded = new();
        PipelineCoordinator coordinator = new(
            data.Store,
            ClassicPageModule.Registry(async (context, token) =>
                new FaultedComponents(await recorded.OpenAsync(context, token), fault)),
            new ForbiddenOnlineEnvironment());
        CollectRequest request = ClassicContentFixture.Request();
        request.CollectionOptions.Properties.Clear();
        ClassicStartRequestBuilder.AddClassicProperties(
            request.CollectionOptions,
            [ClassicComponent.Pages, ClassicComponent.Lists],
            false,
            true,
            false,
            false,
            14);
        PhaseReply ticket = await coordinator.StartPipelineAsync(new() { Collection = request });
        Guid assessment = Guid.Parse(ticket.AssessmentId);
        return (await coordinator.WaitForCompletionAsync(assessment), assessment);
    }

    /// <summary>Delegates every read to the recorded fixture except the retained-component read.</summary>
    private sealed class FaultedComponents(IClassicContentReader inner, Exception fault) : IClassicContentReader
    {
        public Task<ClassicComponentSource> ReadClassicComponentsAsync(WebFacts web, CancellationToken token) =>
            Task.FromException<ClassicComponentSource>(fault);

        public Task<ClassicCollectionScope> ReadSiteSelectionAsync(
            ClassicCollectionOptions options,
            CancellationToken token) =>
            inner.ReadSiteSelectionAsync(options, token);

        public Task<SiteCollectionScope> ReadWebScopesAsync(string siteUrl, CancellationToken token) =>
            inner.ReadWebScopesAsync(siteUrl, token);

        public Task<WebFacts> ReadIdentityAsync(WebScope scope, CancellationToken token) =>
            inner.ReadIdentityAsync(scope, token);

        public Task<WebFacts> ReadWebFactsAsync(WebScope scope, CancellationToken token) =>
            inner.ReadWebFactsAsync(scope, token);

        public Task<WebFileInventory> ReadFileInventoryAsync(
            WebFacts web,
            AcquisitionJournal journal,
            CancellationToken token) =>
            inner.ReadFileInventoryAsync(web, journal, token);

        public Task<FilePageEvidence> ReadFileEnrichmentAsync(
            WebFacts web,
            DiscoveryEvidence page,
            bool skipUsers,
            CancellationToken token) =>
            inner.ReadFileEnrichmentAsync(web, page, skipUsers, token);

        public Task<WebListItemQueries> ReadListItemQueriesAsync(WebFacts web, CancellationToken token) =>
            inner.ReadListItemQueriesAsync(web, token);

        public Task<ListItemBatch> ReadListItemBatchAsync(
            WebFacts web,
            ListItemQueryScope list,
            string? paging,
            bool skipUsers,
            CancellationToken token) =>
            inner.ReadListItemBatchAsync(web, list, paging, skipUsers, token);

        public Task CollectAuditAsync(
            ClassicCollectionScope scope,
            AcquisitionJournal journal,
            CancellationToken token) =>
            inner.CollectAuditAsync(scope, journal, token);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
