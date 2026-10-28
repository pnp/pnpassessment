using FluentAssertions;
using PnP.Core.Model;
using PnP.Core.Model.SharePoint;
using PnP.Core.QueryModel;
using PnP.Core.Services;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Tests.Discovery;
using PnP.Scanning.Core.Tests.Fixtures;
using System.Linq.Expressions;
using Xunit;
using OfflineSdk = PnP.Scanning.Core.Tests.Discovery.SharedDiscoveryContextTests.OfflineSdk;
using OfflineTransport = PnP.Scanning.Core.Tests.Discovery.SharedDiscoveryContextTests.OfflineTransport;

namespace PnP.Scanning.Core.Tests.Scanners.Pages;

[Trait("Category", "ClassicDiscoveryIntegration")]
public sealed class ClassicAssessmentMetadataTests : IClassFixture<ScanContextFixture>
{
    private const string Site = SharedDiscoveryContextTests.WebUrl;
    private readonly ScanContextFixture database;
    public ClassicAssessmentMetadataTests(ScanContextFixture database) => this.database = database;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rich_metadata_uses_one_logical_list_projection_in_existing_context_and_restores_hooks(bool graphFirst)
    {
        using var sdk = new OfflineSdk();
        var context = await sdk.CreateAsync();
        var initializationRequests = sdk.Transport.LogicalRequests.ToArray();
        initializationRequests.Should().HaveCount(2, "only Site/Web identity is initialized");
        sdk.Transport.LogicalRequests.Should().NotContain(entry => OfflineTransport.HasListProjection(entry.Uri));
        context.GraphFirst = graphFirst;
        var observed = new List<string>();
        Action<string> original = observed.Add;
        var mapping = (IDataModelMappingHandler)context.Web;
        mapping.PostMappingHandler = original;
        var priorBatchCount = sdk.Transport.BatchRequests;

        var snapshot = await ClassicAssessmentMetadata.LoadAsync(context, Projection(), CancellationToken.None);

        snapshot.Should().NotBeNull();
        snapshot.WebUrl.Should().Be(new Uri(Site));
        snapshot.Items.Should().ContainSingle().Which.GetProperty("Id").GetGuid()
            .Should().Be(SharedDiscoveryContextTests.ListId);
        snapshot.Items[0].TryGetProperty("Fields", out _).Should().BeFalse("discovery must not retain rich assessment payloads");
        var contentType = snapshot.Items[0].GetProperty("ContentTypes").EnumerateArray().Should().ContainSingle().Which;
        contentType.GetProperty("Id").GetProperty("StringValue").GetString().Should().Be("0x0101");
        contentType.GetProperty("DisplayFormUrl").GetString().Should().Be("/sites/reuse/Documents/Forms/DispForm.aspx");
        contentType.TryGetProperty("Name", out _).Should().BeFalse();
        snapshot.Items[0].GetProperty("DefaultDisplayFormUrl").GetString().Should().Be("/sites/reuse/Documents/Forms/DispForm.aspx");
        var list = context.Web.Lists.AsRequested().Should().ContainSingle().Which;
        list.Id.Should().Be(SharedDiscoveryContextTests.ListId);
        list.Title.Should().Be("Documents");
        list.ItemCount.Should().Be(7);
        list.RootFolder.UniqueId.Should().Be(SharedDiscoveryContextTests.FolderId);
        list.Fields.AsRequested().Should().ContainSingle().Which.InternalName.Should().Be("Title");
        list.ContentTypes.AsRequested().Should().ContainSingle().Which.Name.Should().Be("Document");
        context.Site.Features.AsRequested().Should().BeEmpty();
        sdk.Created.Should().ContainSingle().Which.Should().BeSameAs(context);
        var metadataRequests = sdk.Transport.LogicalRequests.Skip(initializationRequests.Length).ToArray();
        metadataRequests.Should().HaveCount(2).And.OnlyContain(entry => entry.InBatch);
        metadataRequests.Count(entry => OfflineTransport.HasListProjection(entry.Uri)).Should().Be(1);
        Uri.UnescapeDataString(metadataRequests.Single(entry => OfflineTransport.HasListProjection(entry.Uri)).Uri.Query)
            .Should().Contain("StringId", "the SDK content-type key must be part of the real projection, not only the fixture");
        sdk.Transport.BatchRequests.Should().Be(priorBatchCount + 1, "Site and rich Web metadata share one SDK batch");
        observed.Should().ContainSingle(json => json.Contains("\"Lists\""));
        mapping.PostMappingHandler.Should().BeSameAs(original);
        context.GraphFirst.Should().Be(graphFirst);
        sdk.Transport.Unexpected.Should().BeEmpty();
    }

    [Fact]
    public async Task Real_sdk_shared_projection_preserves_content_type_item_pages_without_duplicate_all_lists_get()
    {
        using var sdk = new OfflineSdk();
        var context = await sdk.CreateAsync();
        sdk.Transport.ListResponse = "displayForm";
        var snapshot = await ClassicAssessmentMetadata.LoadAsync(context, Projection(), CancellationToken.None);
        snapshot.Should().NotBeNull();
        using var factory = new PnPContextSharePointAspxRestClientFactory(sdk.Factory, sdk.Authentication,
            sharedContext: context, jsonTransportFactory: ctx => ctx.RestClient.Client);
        using var provider = new SharePointLiveAspxDiscoveryProvider(
            new SharePointLiveAspxDiscoveryOptions("test", "sdk-shared-web", "shared-sdk-v1", new string('a', 64)),
            factory, new AspxWebAcquisitionContext(Guid.Parse(OfflineTransport.SiteId), new Uri(Site),
                Guid.Parse(OfflineTransport.WebId), new Uri(Site), "/sites/reuse", "STS#0"), snapshot);
        var list = (await provider.EnumerateChildrenAsync(provider.RootScope)).ObservedChildren
            .Single(scope => scope.Metadata["role"] == "list");
        var surface = (await provider.EnumerateChildrenAsync(list)).ObservedChildren
            .Single(scope => scope.SourceKind == DiscoverySourceKind.ListItemDisplayFormPages);
        var records = new List<RawDiscoveryRecord>();
        await foreach (var batch in provider.CreateRawSource(surface).ReadBatchesAsync()) records.AddRange(batch.Records);

        var page = records.Should().ContainSingle().Which;
        page.PhysicalLocator.Should().Be("/sites/reuse/Lists/Posts/Post.aspx?ID=7");
        page.ListItemId.Should().Be(7);
        page.ContentTypeId.Should().Be("0x0110007C4E8A");
        page.FileUniqueId.Should().BeNull();
        AssessmentWebDiscovery.Page(Guid.NewGuid(), Site, "/", surface, page).ScopeType.Should().Be("ListItem");
        sdk.Transport.LogicalRequests.Count(entry => OfflineTransport.HasListProjection(entry.Uri)).Should().Be(1);
        sdk.Transport.LogicalRequests.Should().NotContain(entry => entry.Uri.AbsolutePath.EndsWith("/web/lists", StringComparison.OrdinalIgnoreCase));
        sdk.Transport.LogicalRequests.Count(entry => entry.Uri.AbsolutePath.EndsWith("/items", StringComparison.OrdinalIgnoreCase)).Should().Be(1);
        provider.ReferenceCollector.ReadSurfaceEvidence().Single(row => row.RequiredAdapter == "AllListsAuthority")
            .ActualMethod.Should().Be("MODEL");
        context.Web.Lists.AsRequested().Should().ContainSingle().Which.ContentTypes.AsRequested()
            .Should().ContainSingle().Which.StringId.Should().Be("0x0110");
        sdk.Created.Should().ContainSingle().Which.Should().BeSameAs(context);
        sdk.Transport.Unexpected.Should().BeEmpty();
    }

    [Theory]
    [InlineData("empty", true)]
    [InlineData("missing", false)]
    [InlineData("partial", false)]
    [InlineData("nextLink", false)]
    [InlineData("emptyContentTypes", true)]
    [InlineData("missingContentTypes", false)]
    [InlineData("partialContentTypes", false)]
    [InlineData("continuedContentTypes", false)]
    public async Task Actual_sdk_responses_only_produce_shared_authority_when_complete(string response, bool complete)
    {
        using var sdk = new OfflineSdk();
        var context = await sdk.CreateAsync();
        sdk.Transport.ListResponse = response;
        Action<string> original = _ => { };
        var mapping = (IDataModelMappingHandler)context.Web;
        mapping.PostMappingHandler = original;
        context.GraphFirst = true;

        var snapshot = await ClassicAssessmentMetadata.LoadAsync(context, Projection(), CancellationToken.None);

        if (complete)
        {
            snapshot.Should().NotBeNull("explicitly returned empty collections are authoritative");
            if (response == "empty")
            {
                snapshot.Items.Should().BeEmpty();
                context.Web.Lists.AsRequested().Should().BeEmpty();
            }
            else
            {
                snapshot.Items.Should().ContainSingle().Which.GetProperty("ContentTypes").EnumerateArray().Should().BeEmpty();
                context.Web.Lists.AsRequested().Should().ContainSingle().Which.ContentTypes.AsRequested().Should().BeEmpty();
            }
        }
        else snapshot.Should().BeNull("missing rows, partial discovery metadata, or a continuation cannot prove completeness");
        sdk.Transport.LogicalRequests.Count(entry => OfflineTransport.HasListProjection(entry.Uri)).Should().Be(1);
        sdk.Created.Should().ContainSingle();
        mapping.PostMappingHandler.Should().BeSameAs(original);
        context.GraphFirst.Should().BeTrue();
        sdk.Transport.Unexpected.Should().BeEmpty();
    }

    [Fact]
    public async Task Repeated_full_then_empty_load_on_same_context_replaces_modeled_rows_and_snapshot()
    {
        using var sdk = new OfflineSdk();
        var context = await sdk.CreateAsync();
        var observed = new List<string>();
        Action<string> original = observed.Add;
        var mapping = (IDataModelMappingHandler)context.Web;
        mapping.PostMappingHandler = original;
        context.GraphFirst = true;
        var first = await ClassicAssessmentMetadata.LoadAsync(context, Projection(), CancellationToken.None);
        first.Items.Should().ContainSingle();
        context.Web.Lists.AsRequested().Should().ContainSingle();
        mapping.PostMappingHandler.Should().BeSameAs(original);
        context.GraphFirst.Should().BeTrue();
        sdk.Transport.ListResponse = "empty";

        var second = await ClassicAssessmentMetadata.LoadAsync(context, Projection(), CancellationToken.None);

        second.Should().NotBeNull().And.NotBeSameAs(first);
        second.Items.Should().BeEmpty();
        context.Web.Lists.AsRequested().Should().BeEmpty("an empty authoritative response must not leave stale assessment lists");
        first.Items.Should().ContainSingle("the first immutable snapshot must not alias the mutable SDK collection");
        observed.Count(json => json.Contains("\"Lists\"")).Should().Be(2);
        sdk.Transport.LogicalRequests.Count(entry => OfflineTransport.HasListProjection(entry.Uri)).Should().Be(2);
        sdk.Created.Should().ContainSingle();
        mapping.PostMappingHandler.Should().BeSameAs(original);
        context.GraphFirst.Should().BeTrue();
        sdk.Transport.Unexpected.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_sdk_metadata_read_restores_existing_handler_and_graph_preference(bool graphFirst)
    {
        using var sdk = new OfflineSdk();
        var context = await sdk.CreateAsync();
        context.GraphFirst = graphFirst;
        Action<string> original = _ => { };
        var mapping = (IDataModelMappingHandler)context.Web;
        mapping.PostMappingHandler = original;
        sdk.Transport.FailMetadata = true;
        var load = () => ClassicAssessmentMetadata.LoadAsync(context, Projection(), CancellationToken.None);

        await load.Should().ThrowAsync<Exception>();

        mapping.PostMappingHandler.Should().BeSameAs(original);
        context.GraphFirst.Should().Be(graphFirst);
        sdk.Transport.LogicalRequests.Count(entry => OfflineTransport.HasListProjection(entry.Uri)).Should().Be(1);
        sdk.Created.Should().ContainSingle();
        sdk.Transport.Unexpected.Should().BeEmpty("a deliberate offline 403, not an unexpected endpoint, causes the failure");
    }

    [Fact]
    public async Task Canceled_metadata_load_does_not_touch_http_or_existing_hooks()
    {
        using var sdk = new OfflineSdk();
        var context = await sdk.CreateAsync();
        var count = sdk.Transport.LogicalRequests.Count;
        Action<string> original = _ => { };
        var mapping = (IDataModelMappingHandler)context.Web;
        mapping.PostMappingHandler = original;
        context.GraphFirst = true;
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var load = () => ClassicAssessmentMetadata.LoadAsync(context, Projection(), cancel.Token);
        await load.Should().ThrowAsync<OperationCanceledException>();
        sdk.Transport.LogicalRequests.Should().HaveCount(count);
        mapping.PostMappingHandler.Should().BeSameAs(original);
        context.GraphFirst.Should().BeTrue();
    }

    [Fact]
    public async Task Full_acquisition_loads_once_then_passes_identical_snapshot_to_discovery()
    {
        using var sdk = new OfflineSdk();
        var context = await sdk.CreateAsync();
        var order = new List<string>();
        var pages = new List<ClassicPageDiscovery> { Page(Guid.NewGuid()) };
        SharePointListMetadataSnapshot loaded = null;
        var result = await ClassicAssessmentMetadata.AcquirePagesAsync(false, async () =>
        {
            order.Add("metadata");
            loaded = await ClassicAssessmentMetadata.LoadAsync(context, Projection(), CancellationToken.None);
            return loaded;
        }, snapshot =>
        {
            order.Add("discovery");
            snapshot.Should().BeSameAs(loaded).And.NotBeNull();
            snapshot.Items.Should().ContainSingle();
            context.Web.Lists.AsRequested().Should().ContainSingle();
            sdk.Transport.LogicalRequests.Count(entry => OfflineTransport.HasListProjection(entry.Uri)).Should().Be(1);
            return Task.FromResult(pages);
        });
        order.Should().Equal("metadata", "discovery");
        result.Should().BeSameAs(pages);
        sdk.Created.Should().ContainSingle();
        sdk.Transport.Unexpected.Should().BeEmpty();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("nextLink")]
    public async Task Full_acquisition_passes_null_for_incomplete_http_snapshot_instead_of_skipping_discovery(string response)
    {
        using var sdk = new OfflineSdk();
        var context = await sdk.CreateAsync();
        sdk.Transport.ListResponse = response;
        var order = new List<string>();
        var pages = new List<ClassicPageDiscovery> { Page(Guid.NewGuid()) };
        var result = await ClassicAssessmentMetadata.AcquirePagesAsync(false, async () =>
        {
            order.Add("metadata");
            return await ClassicAssessmentMetadata.LoadAsync(context, Projection(), CancellationToken.None);
        }, snapshot =>
        {
            order.Add("fallback-discovery");
            snapshot.Should().BeNull();
            return Task.FromResult(pages);
        });
        order.Should().Equal("metadata", "fallback-discovery");
        result.Should().BeSameAs(pages);
        sdk.Transport.LogicalRequests.Count(entry => OfflineTransport.HasListProjection(entry.Uri)).Should().Be(1);
        sdk.Transport.Unexpected.Should().BeEmpty();
    }

    [Fact]
    public async Task Home_acquisition_discovers_target_first_then_loads_normal_rich_lists_once()
    {
        using var sdk = new OfflineSdk();
        var context = await sdk.CreateAsync();
        var order = new List<string>();
        var pages = new List<ClassicPageDiscovery> { Page(Guid.NewGuid()) };
        var result = await ClassicAssessmentMetadata.AcquirePagesAsync(true, async () =>
        {
            order.Add("metadata");
            return await ClassicAssessmentMetadata.LoadAsync(context, Projection(), CancellationToken.None);
        }, async snapshot =>
        {
            order.Add("targeted-discovery");
            snapshot.Should().BeNull("HOME must not receive an all-lists authority");
            sdk.Transport.LogicalRequests.Should().NotContain(entry => OfflineTransport.HasListProjection(entry.Uri));
            using var client = new PnPContextSharePointAspxRestClient(context, ownsContext: false);
            var welcome = await client.ReadWelcomePageAsync();
            welcome.Outcome.Should().Be(DiscoveryTerminalOutcome.Complete);
            welcome.Value.Should().Be("SitePages/Home.aspx");
            sdk.Transport.LogicalRequests.Should().NotContain(entry => OfflineTransport.HasListProjection(entry.Uri));
            return pages;
        });
        order.Should().Equal("targeted-discovery", "metadata");
        result.Should().BeSameAs(pages);
        sdk.Transport.LogicalRequests.Count(entry => OfflineTransport.HasListProjection(entry.Uri)).Should().Be(1,
            "normal rich assessment metadata still loads once after targeted HOME discovery");
        sdk.Transport.LogicalRequests.Should().NotContain(entry => entry.Uri.AbsolutePath.EndsWith("/Lists",
            StringComparison.OrdinalIgnoreCase));
        sdk.Created.Should().ContainSingle();
        sdk.Transport.Unexpected.Should().BeEmpty();
    }

    [Fact]
    public async Task Failed_http_metadata_runs_discovery_before_page_failure_finalization_and_rethrows_original()
    {
        using var sdk = new OfflineSdk();
        var context = await sdk.CreateAsync();
        sdk.Transport.FailMetadata = true;
        var scan = Guid.NewGuid();
        var writer = new AssessmentDiscoveryWriter(database.CreateContext);
        Exception original = null;
        var fallbackCalls = 0;
        var run = () => ClassicAssessmentInitialization.ExecuteAsync(
            () => ClassicAssessmentMetadata.LoadAsync(context, Projection(), CancellationToken.None),
            writer, scan, Site, "/", CancellationToken.None, beforeFailureFinalization: async error =>
            {
                original = error;
                fallbackCalls++;
                (await writer.ReadPagesAsync(scan, Site, "/")).Should().BeEmpty();
                await writer.WriteAsync(new[] { Page(scan) });
                (await writer.ReadPagesAsync(scan, Site, "/")).Should().ContainSingle().Which.AssessmentStatus.Should().BeNull();
            });
        var thrown = await run.Should().ThrowAsync<Exception>();
        thrown.Which.Should().BeSameAs(original);
        fallbackCalls.Should().Be(1);
        var stored = (await writer.ReadPagesAsync(scan, Site, "/")).Should().ContainSingle().Which;
        stored.DiscoveryStatus.Should().Be("Discovered");
        stored.AssessmentStatus.Should().Be("Failed");
        stored.ErrorStage.Should().Be("AssessmentInitialization");
        stored.FileUniqueId.Should().Be(SharedDiscoveryContextTests.FolderId);
        sdk.Transport.LogicalRequests.Count(entry => OfflineTransport.HasListProjection(entry.Uri)).Should().Be(1);
        sdk.Transport.Unexpected.Should().BeEmpty();
    }

    [Fact]
    public async Task Exhausted_response_body_retries_run_fallback_only_after_last_attempt()
    {
        var scan = Guid.NewGuid();
        var writer = new AssessmentDiscoveryWriter(database.CreateContext);
        var order = new List<string>();
        var failure = new HttpRequestException(HttpRequestError.ResponseEnded, "response ended");
        var run = () => ClassicAssessmentInitialization.ExecuteAsync<object>(() =>
        {
            order.Add("attempt");
            return Task.FromException<object>(failure);
        }, writer, scan, Site, "/", CancellationToken.None, delay: (_, _) => Task.CompletedTask,
            beforeFailureFinalization: async error =>
            {
                order.Should().Equal("attempt", "attempt", "attempt");
                error.Should().BeSameAs(failure);
                order.Add("fallback");
                await writer.WriteAsync(new[] { Page(scan) });
            });
        (await run.Should().ThrowAsync<HttpRequestException>()).Which.Should().BeSameAs(failure);
        order.Should().Equal("attempt", "attempt", "attempt", "fallback");
        (await writer.ReadPagesAsync(scan, Site, "/")).Should().ContainSingle().Which.AssessmentStatus.Should().Be("Failed");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Genuine_cancellation_never_runs_fallback_or_marks_existing_pages_failed(bool cancelDuringLoad)
    {
        var scan = Guid.NewGuid();
        var writer = new AssessmentDiscoveryWriter(database.CreateContext);
        await writer.WriteAsync(new[] { Page(scan) });
        using var cancel = new CancellationTokenSource();
        if (!cancelDuringLoad) cancel.Cancel();
        var attempts = 0;
        var fallbacks = 0;
        var run = () => ClassicAssessmentInitialization.ExecuteAsync<object>(() =>
        {
            attempts++;
            cancel.Cancel();
            return Task.FromCanceled<object>(cancel.Token);
        }, writer, scan, Site, "/", cancel.Token, beforeFailureFinalization: _ =>
        {
            fallbacks++;
            return Task.CompletedTask;
        });
        await run.Should().ThrowAsync<OperationCanceledException>();
        attempts.Should().Be(cancelDuringLoad ? 1 : 0);
        fallbacks.Should().Be(0);
        (await writer.ReadPagesAsync(scan, Site, "/")).Should().ContainSingle().Which.AssessmentStatus.Should().BeNull();
    }

    [Fact]
    public async Task Cancellation_during_raw_fallback_does_not_finalize_discovered_pages_as_failed()
    {
        var scan = Guid.NewGuid();
        var writer = new AssessmentDiscoveryWriter(database.CreateContext);
        using var cancel = new CancellationTokenSource();
        var run = () => ClassicAssessmentInitialization.ExecuteAsync<object>(
            () => Task.FromException<object>(new IOException("metadata unavailable")),
            writer, scan, Site, "/", cancel.Token, beforeFailureFinalization: async _ =>
            {
                await writer.WriteAsync(new[] { Page(scan) });
                cancel.Cancel();
            });

        await run.Should().ThrowAsync<OperationCanceledException>();
        var stored = (await writer.ReadPagesAsync(scan, Site, "/")).Should().ContainSingle().Which;
        stored.DiscoveryStatus.Should().Be("Discovered");
        stored.AssessmentStatus.Should().BeNull("cancellation during fallback must leave the page resumable");
    }

    private static PnPContextOptions Projection() => new()
    {
        AdditionalSitePropertiesOnCreate = new Expression<Func<ISite, object>>[]
        {
            site => site.Features.QueryProperties(feature => feature.DefinitionId)
        },
        AdditionalWebPropertiesOnCreate = new Expression<Func<IWeb, object>>[]
        {
            web => web.LastItemUserModifiedDate,
            web => web.Lists.QueryProperties(list => list.Title, list => list.Hidden, list => list.IsCatalog,
                list => list.BaseType, list => list.TemplateType, list => list.DefaultViewUrl, list => list.DefaultDisplayFormUrl,
                list => list.RootFolder.QueryProperties(folder => folder.ServerRelativeUrl, folder => folder.UniqueId),
                list => list.ItemCount,
                list => list.Fields.QueryProperties(field => field.InternalName, field => field.Title,
                    field => field.FieldTypeKind, field => field.TypeAsString),
                list => list.ContentTypes.QueryProperties(contentType => contentType.Id, contentType => contentType.StringId,
                    contentType => contentType.Name, contentType => contentType.DisplayFormUrl))
        }
    };

    private static ClassicPageDiscovery Page(Guid scan) => new()
    {
        ScanId = scan, SiteUrl = Site, WebUrl = "/", RecordKey = "page:offline",
        RowType = "Page", ScopeType = "File", Url = "/sites/reuse/SitePages/Home.aspx", FileName = "Home.aspx",
        SiteCollectionId = Guid.Parse(OfflineTransport.SiteId), WebId = Guid.Parse(OfflineTransport.WebId),
        FileUniqueId = SharedDiscoveryContextTests.FolderId, DiscoveryStatus = "Discovered",
        ObservedAtUtc = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc)
    };
}
