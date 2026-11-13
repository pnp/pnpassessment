using PnP.Scanning.Core.ClassicPages.Contracts.Shared;
using PnP.Scanning.Core.ClassicPages.Collection.Module;
using PnP.Scanning.Core.ClassicPages.Analysis.Module;
using PnP.Scanning.Core.ClassicPages;
#nullable enable
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.ClassicPages.Analysis.Page;
using PnP.Scanning.Core.ClassicPages.Collection.Page;
using PnP.Scanning.Core.Pipeline.Collection.Shared;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.ClassicPages.Contracts.List;
using PnP.Scanning.Core.ClassicPages.Contracts.Page;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Pipeline.Orchestration;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using Xunit;

namespace PnP.Scanning.Core.Tests.Pipeline;

/// <summary>
/// Verifies production collection and detached replay with tenant-neutral recorded evidence.
/// </summary>
[Trait("Category", "Pipeline")]
public sealed class ClassicPageRuntimeTests
{
    /// <summary>
    /// Verifies that foreign assessment file evidence cannot seal or start analysis.
    /// </summary>
    [Fact]
    public async Task Foreign_assessment_file_evidence_cannot_seal_or_start_analysis()
    {
        using StoreCase? data = new StoreCase("classicpage-foreign-assessment");
        ClassicContentFixture? source =
            new ClassicContentFixture { IncludePhysicalFacts = true, ExtraFileAssessment = Guid.NewGuid() };
        PipelineCoordinator? coordinator = new PipelineCoordinator(
            data.Store,
            ClassicPageModule.Registry(source.OpenAsync),
            new ForbiddenOnlineEnvironment());
        PhaseReply? ticket =
            await coordinator.StartPipelineAsync(new() { Collection = ClassicContentFixture.Request() });
        Guid id = Guid.Parse(ticket.AssessmentId);
        Assert.Equal(ScanStatus.Terminated, await coordinator.WaitForCompletionAsync(id));
        using ScanContext? db = data.Store.CreateContext(id);
        Assert.False((await db.SourceSnapshots.SingleAsync()).IsSealed);
        Assert.Empty(await db.AnalysisRuns.ToListAsync());
        Assert.Contains("different assessment", (await data.Store.LatestRootAsync(id))!.LastError);
    }

    /// <summary>
    /// Verifies that each csom scope disposes its transport without disposing the shared sdk client.
    /// </summary>
    [Fact]
    public async Task Each_csom_scope_disposes_its_transport_without_disposing_the_shared_sdk_client()
    {
        using HttpClient? shared = new HttpClient(new RecordedHandler());
        HttpClient? transport = new HttpClient(new CollectionHttpClient(shared, default));
        using (new ClassicContentReader.CsomLease(
                   new Microsoft.SharePoint.Client.ClientContext("https://contoso.invalid"),
                   transport))
        {
            using HttpResponseMessage? response = await transport.GetAsync("https://contoso.invalid");
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        }

        await Assert.ThrowsAsync<ObjectDisposedException>(() => transport.GetAsync("https://contoso.invalid"));
        using HttpResponseMessage? after = await shared.GetAsync("https://contoso.invalid");
        Assert.Equal(System.Net.HttpStatusCode.OK, after.StatusCode);
    }

    /// <summary>
    /// Returns synthetic HTTP responses without reaching a network service.
    /// </summary>
    private sealed class RecordedHandler : HttpMessageHandler
    {
        /// <summary>
        /// Returns a synthetic successful response without making an HTTP request.
        /// </summary>
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }

    /// <summary>
    /// Verifies that denied discovery is not an authoritative empty result.
    /// </summary>
    [Fact]
    public async Task Denied_discovery_is_not_an_authoritative_empty_result()
    {
        using StoreCase? data = new StoreCase("classicpage-denied-discovery");
        ClassicContentFixture? source = new ClassicContentFixture { DeniedDiscovery = true };
        PipelineCoordinator? coordinator = new PipelineCoordinator(
            data.Store,
            ClassicPageModule.Registry(source.OpenAsync),
            new ForbiddenOnlineEnvironment());
        PhaseReply? ticket =
            await coordinator.StartPipelineAsync(new() { Collection = ClassicContentFixture.Request() });
        Guid id = Guid.Parse(ticket.AssessmentId);
        Assert.Equal(ScanStatus.Finished, await coordinator.WaitForCompletionAsync(id));
        using ScanContext? db = data.Store.CreateContext(id);
        Core.Storage.Pipeline.AnalysisResultRow? result = (await db.AnalysisResults.ToListAsync()).Single(row =>
            new VersionedJson(row.PayloadJson).Value.GetProperty("kind").GetString() == "FileInventory");
        Assert.Equal(AnalysisOutcome.Unknown, result.Outcome);
        JsonElement value = new VersionedJson(result.PayloadJson).Value;
        Assert.Empty(value.GetProperty("files").EnumerateArray());
        Assert.Equal("Denied", value.GetProperty("coverage")[0].GetProperty("DiscoveryStatus").GetString());
        ClassicPageDiscovery summary =
            await db.ClassicPageDiscoveries.SingleAsync(row => row.RecordKey == "summary:coverage");
        Assert.Equal("Incomplete", summary.DiscoveryStatus);
    }

    /// <summary>
    /// Verifies that denied optional web metadata retains discovered native files.
    /// </summary>
    [Fact]
    public async Task Denied_optional_web_metadata_retains_discovered_native_files()
    {
        using StoreCase? data = new StoreCase("classicpage-partial-web");
        ClassicContentFixture? source =
            new ClassicContentFixture { IncludePhysicalFacts = true, FailEnrichment = true };
        PipelineCoordinator? coordinator = new PipelineCoordinator(
            data.Store,
            ClassicPageModule.Registry(source.OpenAsync),
            new ForbiddenOnlineEnvironment());
        PhaseReply? ticket =
            await coordinator.StartPipelineAsync(new() { Collection = ClassicContentFixture.Request() });
        Guid id = Guid.Parse(ticket.AssessmentId);
        ScanStatus status = await coordinator.WaitForCompletionAsync(id);
        Assert.True(status == ScanStatus.Finished, (await data.Store.LatestRootAsync(id))!.LastError);
        using ScanContext? db = data.Store.CreateContext(id);
        Assert.All(
            await db.AnalysisRuns.AsNoTracking().ToListAsync(),
            run =>
            {
                Assert.NotNull(run.ReportDigest);
                Assert.NotNull(run.ReportPublishedAtUtc);
            });
        Core.Storage.Pipeline.SourceObservationRow? discovery =
            await db.SourceObservations.SingleAsync(x => x.SourceIdentity.StartsWith("FileInventory:"));
        Assert.Equal(8, CollectionJson.Read<WebFileInventory>(discovery.RawBytes).Files!.Length);
        Assert.True(await db.AnalysisResults.AnyAsync(x => x.Outcome == AnalysisOutcome.Unknown));
        Assert.DoesNotContain(source.Calls.Keys, key => key.StartsWith("page:", StringComparison.Ordinal));
    }

    /// <summary>
    /// Verifies that a page hosting a hidden list view stays a content page and a list's own view page is a list view.
    /// </summary>
    [Fact]
    public void A_page_hosting_a_hidden_list_view_stays_a_content_page_and_a_lists_own_view_page_is_a_list_view()
    {
        Guid library = Guid.NewGuid();
        Guid other = Guid.NewGuid();
        AspxPageEnricher.ViewRegistration Visible(Guid list) => new(list, false);
        AspxPageEnricher.ViewRegistration Hidden(Guid list) => new(list, true);

        // A wiki, web part or publishing page whose list view web parts show other lists.
        Assert.False(AspxPageEnricher.IsListViewPage([Hidden(other), Hidden(other)], library, true));
        // A page at the root of the web, outside every list.
        Assert.False(AspxPageEnricher.IsListViewPage([Hidden(other)], null, false));
        // A page stored as an item of a library that shows a view of that same library.
        Assert.False(AspxPageEnricher.IsListViewPage([Hidden(library)], library, true));
        // The list's own view pages, visible or hidden.
        Assert.True(AspxPageEnricher.IsListViewPage([Visible(library)], library, false));
        Assert.True(AspxPageEnricher.IsListViewPage([Hidden(library)], library, false));
        // One visible view is enough, wherever its page is stored.
        Assert.True(AspxPageEnricher.IsListViewPage([Visible(other)], library, true));
        Assert.True(AspxPageEnricher.IsListViewPage([Hidden(other), Visible(other)], null, false));
        Assert.False(AspxPageEnricher.IsListViewPage([], library, false));
    }

    /// <summary>
    /// Verifies that generic safety errors are not misclassified as the specific form identity failure.
    /// </summary>
    [Fact]
    public async Task Generic_safety_errors_are_not_misclassified_as_the_specific_form_identity_failure()
    {
        Assert.False(
            AspxPageEnricher.IsNullGuidServerError(
                "Microsoft.SharePoint.SPException",
                "The type or namespace is not allowed by SafeControls."));
        Assert.False(AspxPageEnricher.IsNullGuidServerError("System.ArgumentNullException", "Parameter name: other"));
        Assert.True(
            AspxPageEnricher.IsNullGuidServerError(
                "System.ArgumentNullException",
                "Value cannot be null.\r\nParameter name: g"));
        int calls = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => AspxPageEnricher.ReadWithFormIdentityFallbackAsync(
            false,
            Guid.NewGuid(),
            _ =>
            {
                calls++;
                throw new InvalidDataException("SafeControls rejected the request.");
            },
            default));
        Assert.Equal(1, calls);
    }

    /// <summary>
    /// Verifies that typed fields preserve null and nested values and reject unknown types.
    /// </summary>
    [Fact]
    public void Typed_fields_preserve_null_and_nested_values_and_reject_unknown_types()
    {
        Assert.Null(SourceField.Of("Null", null).ToValue());
        Assert.Equal(Guid.Empty, SourceField.Of("Guid", Guid.Empty).ToValue());
        Assert.Equal(long.MaxValue, SourceField.Of("Int64", long.MaxValue).ToValue());
        JsonElement value = (JsonElement)SourceField.Of("Json", new { original = new[] { "one", "two" } }).ToValue()!;
        Assert.Equal(2, value.GetProperty("original").GetArrayLength());
        Assert.Throws<InvalidDataException>(() => SourceField.Of("Unmapped", 1).ToValue());
    }

    /// <summary>
    /// Verifies that production module replays native files actual blog items and audit without online access.
    /// </summary>
    [Fact]
    public async Task Production_module_replays_native_files_actual_blog_items_and_audit_without_online_access()
    {
        using StoreCase? data = new StoreCase("classicpage-replay");
        ClassicContentFixture? source = new ClassicContentFixture
        {
            IncludePhysicalFacts = true,
            IncludeBlog = true,
            IncludeSecondBlog = true,
            PostsOnNonBlogWeb = true,
            IncludeRetiredFile = true,
        };
        ForbiddenOnlineEnvironment? environment = new ForbiddenOnlineEnvironment();
        PipelineCoordinator? coordinator = new PipelineCoordinator(
            data.Store,
            ClassicPageModule.Registry(source.OpenAsync),
            environment);
        PhaseReply? ticket =
            await coordinator.StartPipelineAsync(new() { Collection = ClassicContentFixture.Request(usage: true) });
        Guid id = Guid.Parse(ticket.AssessmentId);
        Assert.Equal(ScanStatus.Finished, await coordinator.WaitForCompletionAsync(id));
        using ScanContext? db = data.Store.CreateContext(id);
        Dictionary<Guid, string>? original = await db.AnalysisResults.AsNoTracking()
            .ToDictionaryAsync(x => x.ObservationId, x => x.PayloadJson);
        List<Core.Storage.Pipeline.SourceObservationRow>? observations =
            await db.SourceObservations.AsNoTracking().ToListAsync();
        ListItemBatch[]? blogs = observations
            .Where(x => new VersionedJson(x.MetadataJson).Value.GetProperty("kind").GetString() == "ListItems")
            .Select(x => CollectionJson.Read<ListItemBatch>(x.RawBytes))
            .ToArray();
        Assert.Equal(4, blogs.Sum(x => x.Items.Length));
        Assert.Equal(2, blogs.Select(x => x.ListId).Distinct().Count());
        JsonElement[]? pages = original.Values.Select(x => new VersionedJson(x).Value)
            .Where(x => x.GetProperty("kind").GetString() == "ListItems")
            .SelectMany(x => x.GetProperty("pages").EnumerateArray())
            .ToArray();
        Assert.Contains(
            pages,
            page => page.GetProperty("PageUrl").GetString() == "/sites/a/Lists/Posts/Post.aspx?ID=100");
        Assert.DoesNotContain(
            pages,
            page => page.GetProperty("PageUrl").GetString() == "/sites/a/SitePages/post.pointpub");
        JsonElement discovery = original.Values.Select(x => new VersionedJson(x).Value)
            .Single(x => x.GetProperty("kind").GetString() == "FileInventory");
        Assert.Equal(9, discovery.GetProperty("files").GetArrayLength());
        Assert.All(
            discovery.GetProperty("files").EnumerateArray(),
            file =>
            {
                Assert.Equal(ClassicContentFixture.SiteCollectionId, file.GetProperty("SiteCollectionId").GetGuid());
                Assert.NotEqual(Guid.Empty, file.GetProperty("FileUniqueId").GetGuid());
            });
        Assert.Contains(await db.AnalysisResults.ToListAsync(), x => x.Outcome == AnalysisOutcome.Failed);
        SnapshotInfo? manifest = await data.Store.OpenSnapshotAsync(id, Guid.Parse(ticket.SnapshotId));
        Dictionary<string, int>? calls = source.Calls.ToDictionary(x => x.Key, x => x.Value);
        environment.ForbidRestore = true;
        ModuleRegistry? detached = new ModuleRegistry(
            analysis:
            [
                new(
                    ClassicPageModule.Key,
                    ClassicPageModule.RuleVersion,
                    [ClassicPageModule.InputVersion],
                    () => new ClassicPageAnalysisModule(),
                    ClassicPageRuleMetadata.Validate,
                    ClassicPageRuleMetadata.Pin)
            ],
            current: new Dictionary<string, string> { [ClassicPageModule.Key] = ClassicPageModule.RuleVersion });
        PipelineCoordinator? offline = new PipelineCoordinator(new(data.DirectoryPath), detached, environment);
        PhaseReply? replay =
            await offline.AnalyzeAsync(new() { Id = ticket.AssessmentId, SnapshotId = ticket.SnapshotId });
        Assert.Equal(ScanStatus.Finished, await offline.WaitForCompletionAsync(id));
        Guid replayId = Guid.Parse(replay.AnalysisRunId);
        List<Core.Storage.Pipeline.AnalysisResultRow>? repeated =
            await db.AnalysisResults.AsNoTracking().Where(x => x.AnalysisRunId == replayId).ToListAsync();
        Assert.Equal(original.Count, repeated.Count);
        Assert.All(repeated, result => Assert.Equal(original[result.ObservationId], result.PayloadJson));
        Assert.Equal(
            manifest.ManifestDigest,
            (await data.Store.OpenSnapshotAsync(id, manifest.SnapshotId)).ManifestDigest);
        Assert.Equal(calls.OrderBy(x => x.Key), source.Calls.OrderBy(x => x.Key));
        Assert.Equal(0, environment.OnlineRequests);
        Assert.Equal(0, environment.ProtectionRequests);
    }

    /// <summary>
    /// Verifies that production collection pause reuses committed discovery receipts.
    /// </summary>
    [Fact]
    public async Task Production_collection_pause_reuses_committed_discovery_receipts()
    {
        using StoreCase? data = new StoreCase("classicpage-resume");
        ClassicContentFixture? source =
            new ClassicContentFixture { BlockAt = "discovery", IncludePhysicalFacts = true };
        PipelineCoordinator? coordinator = new PipelineCoordinator(
            data.Store,
            ClassicPageModule.Registry(source.OpenAsync),
            new ForbiddenOnlineEnvironment());
        PhaseReply? ticket = await coordinator.CollectAsync(ClassicContentFixture.Request());
        Guid id = Guid.Parse(ticket.AssessmentId);
        await source.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await coordinator.PauseAsync(id, false);
        source.BlockAt = null;
        source.Revision = "later-response";
        PipelineCoordinator? reopened = new PipelineCoordinator(
            new(data.DirectoryPath),
            ClassicPageModule.Registry(source.OpenAsync),
            new ForbiddenOnlineEnvironment());
        await reopened.RestartAsync(id);
        Assert.Equal(ScanStatus.Finished, await reopened.WaitForCompletionAsync(id));
        Assert.Equal(1, source.Calls["sites"]);
        Assert.Equal(1, source.Calls["webs"]);
        using ScanContext? db = data.Store.CreateContext(id);
        Core.Storage.Pipeline.SourceObservationRow? first =
            await db.SourceObservations.SingleAsync(x => x.SourceIdentity == "DiscoveryRequest:batch:0");
        Assert.Equal(
            "original",
            new VersionedJson(Encoding.UTF8.GetString(first.RawBytes!)).Value.GetProperty("Revision").GetString());
        Assert.Single(await db.SourceSnapshots.ToListAsync());
    }

    /// <summary>
    /// Verifies that original source bytes survive storage and drive offline classification.
    /// </summary>
    [Fact]
    public async Task Original_source_bytes_survive_storage_and_drive_offline_classification()
    {
        byte[]? bytes = Encoding.Unicode.GetPreamble()
            .Concat(Encoding.Unicode.GetBytes("<%@ Page Inherits=\"Microsoft.SharePoint.WebPartPages.WebPartPage\" %>"))
            .ToArray();
        AspxSourceObservation? acquired = await AspxSourceReader.ReadStreamAsync(new MemoryStream(bytes), default);
        Assert.Equal(bytes, acquired.Bytes);
        PhysicalFileFacts? file = new PhysicalFileFacts
        {
            SiteCollectionId = Guid.NewGuid(),
            FileUniqueId = Guid.NewGuid(),
            WebId = Guid.NewGuid(),
            SiteUrl = "https://contoso.sharepoint.com/sites/a",
            WebUrl = "/",
            ServerRelativeUrl = "/sites/a/_catalogs/masterpage/custom.aspx",
            AspxEvidenceJson = JsonSerializer.Serialize(
                new AspxPageEvidence(
                    "Available",
                    null,
                    null,
                    "PendingAnalysis",
                    null,
                    null,
                    116,
                    true,
                    false,
                    false,
                    "Complete",
                    DateTime.UnixEpoch,
                    acquired.Bytes)),
        };
        using StoreCase? data = new StoreCase("classicpage-bytes");
        CollectedObservation? observation = new CollectedObservation(
            Guid.NewGuid(),
            $"file|{file.SiteCollectionId:D}|{file.FileUniqueId:D}",
            null,
            AcquisitionStatus.Complete,
            VersionedJson.From(new { kind = "PhysicalFile" }),
            CollectionJson.Bytes(file));
        (Guid Assessment, Guid Snapshot, Guid Run, ISnapshotWriter Writer) seed =
            await data.UnsealedAsync([observation]);
        await seed.Writer.SealAsync(default);
        PhysicalFileFacts? saved = CollectionJson.Read<PhysicalFileFacts>(
            (await data.Store.SnapshotReader(seed.Assessment, seed.Snapshot)
                .ReadAsync(observation.ObservationId, default)).Artifact.GetBytes());
        Assert.Equal(bytes, JsonSerializer.Deserialize<AspxPageEvidence>(saved.AspxEvidenceJson!)!.OriginalBytes);
        PhysicalFileAnalysis? analysis = PhysicalFileAnalysisRules.Analyze(saved);
        Assert.Equal("catalog_masterpage", analysis.Classification!.ProductFamily);
        Assert.Equal("Classic", analysis.Classification.Experience);
        Assert.Equal("Excluded", analysis.Classification.ImpactStatus);
        Assert.Equal("Declared", analysis.HandlerResolution);
    }

    /// <summary>
    /// Verifies that a source read sends one authenticated request addressed by the file id.
    /// </summary>
    [Fact]
    public async Task A_source_read_sends_one_authenticated_request_addressed_by_the_file_id()
    {
        Guid fileId = Guid.Parse("00000000-0000-0000-0000-000000000042");
        byte[] bytes = Encoding.UTF8.GetBytes(
            "<%@ Page Inherits=\"Microsoft.SharePoint.WebPartPages.WikiEditPage\" %>");
        SourceHandler handler = new(() =>
            new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        using HttpClient client = new HttpClient(handler);
        int authenticated = 0;
        AspxSourceObservation source = await AspxSourceReader.ReadAsync(
            client,
            request =>
            {
                authenticated++;
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer recorded");
                return Task.CompletedTask;
            },
            new Uri("https://contoso.sharepoint.com/sites/a/sub"),
            fileId,
            bytes.Length,
            default);
        Assert.Equal("Available", source.State);
        Assert.Equal(bytes, source.Bytes);
        Assert.Equal(1, authenticated);
        (HttpMethod method, string address, string? authorization) = Assert.Single(handler.Sent);
        Assert.Equal(HttpMethod.Get, method);
        Assert.Equal(
            "https://contoso.sharepoint.com/sites/a/sub/_api/web/" +
            "GetFileById('00000000-0000-0000-0000-000000000042')/$value",
            address);
        Assert.Equal("Bearer recorded", authorization);
        Assert.Equal(
            "https://contoso.sharepoint.com/_api/web/GetFileById('00000000-0000-0000-0000-000000000042')/$value",
            AspxSourceReader.SourceUri(new Uri("https://contoso.sharepoint.com/"), fileId).AbsoluteUri);
    }

    /// <summary>
    /// Verifies that a source read bounds the file before and after its request and reports a refused request.
    /// </summary>
    [Fact]
    public async Task A_source_read_bounds_the_file_before_and_after_its_request_and_reports_a_refused_request()
    {
        Guid fileId = Guid.NewGuid();
        Uri web = new Uri("https://contoso.sharepoint.com/sites/a");
        Func<HttpRequestMessage, Task> authenticate = _ => Task.CompletedTask;
        SourceHandler unsent = new(() => new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        using HttpClient first = new HttpClient(unsent);
        AspxSourceObservation observedTooLarge = await AspxSourceReader.ReadAsync(
            first,
            authenticate,
            web,
            fileId,
            AspxSourceParser.MaximumSourceBytes + 1L,
            default);
        Assert.Equal("TooLarge", observedTooLarge.State);
        Assert.Empty(unsent.Sent);

        SourceHandler large = new(() => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[AspxSourceParser.MaximumSourceBytes + 1])
        });
        using HttpClient second = new HttpClient(large);
        AspxSourceObservation returnedTooLarge =
            await AspxSourceReader.ReadAsync(second, authenticate, web, fileId, null, default);
        Assert.Equal("TooLarge", returnedTooLarge.State);
        Assert.Null(returnedTooLarge.Bytes);
        Assert.Single(large.Sent);

        using HttpClient denied = new HttpClient(
            new SourceHandler(() => new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            AspxSourceReader.ReadAsync(denied, authenticate, web, fileId, 10, default));
        using HttpClient failed = new HttpClient(
            new SourceHandler(() => new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)));
        HttpRequestException error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            AspxSourceReader.ReadAsync(failed, authenticate, web, fileId, 10, default));
        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, error.StatusCode);
    }

    /// <summary>
    /// Records each request a source read sends and answers it with a synthetic response.
    /// </summary>
    private sealed class SourceHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal List<(HttpMethod Method, string Address, string? Authorization)> Sent { get; } = [];

        /// <summary>
        /// Notes the request method, address and authorization, then returns the synthetic response.
        /// </summary>
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Sent.Add(
                (request.Method, request.RequestUri!.AbsoluteUri,
                    request.Headers.TryGetValues("Authorization", out IEnumerable<string>? values)
                        ? string.Join(",", values)
                        : null));
            return Task.FromResult(respond());
        }
    }

    /// <summary>
    /// Verifies that a registered list form is recorded and classified without its source.
    /// </summary>
    [Fact]
    public void A_registered_list_form_is_recorded_and_classified_without_its_source()
    {
        AspxPageEvidence evidence =
            AspxPageEnricher.SourceNotRequested(100, false, false, true, "Complete", DateTime.UnixEpoch);
        Assert.Equal("NotRequested", evidence.SourceReadState);
        Assert.Equal("NotRequested", evidence.HandlerResolution);
        Assert.Null(evidence.OriginalBytes);
        Assert.Null(evidence.SourceError);
        Assert.True(evidence.IsListForm);
        PhysicalFileAnalysis analysis = PhysicalFileAnalysisRules.Analyze(
            new PhysicalFileFacts
            {
                SiteCollectionId = Guid.NewGuid(),
                FileUniqueId = Guid.NewGuid(),
                WebId = Guid.NewGuid(),
                SiteUrl = "https://contoso.sharepoint.com/sites/a",
                WebUrl = "/",
                ServerRelativeUrl = "/sites/a/Lists/Tasks/NewForm.aspx",
                AspxEvidenceJson = JsonSerializer.Serialize(evidence),
            });
        Assert.Equal("List", analysis.Classification!.ProductFamily);
        Assert.Equal("Inapplicable", analysis.Classification.Experience);
        Assert.Equal("Excluded", analysis.Classification.ImpactStatus);
        Assert.Null(analysis.HandlerResolution);
        Assert.Null(analysis.SourceSha256);
    }

    /// <summary>
    /// Verifies that only a registered list view or form without an item skips its second read.
    /// </summary>
    [Fact]
    public void Only_a_registered_list_view_or_form_without_an_item_skips_its_second_read()
    {
        DiscoveryEvidence[] rows = new ClassicContentFixture().DiscoveryRows();
        DiscoveryEvidence withItem = rows[1];
        DiscoveryEvidence withoutItem = rows[5];
        Assert.NotNull(withItem.ListItemId);
        Assert.Null(withoutItem.ListItemId);

        static PhysicalFileFacts Facts(DiscoveryEvidence row, bool? view, bool? form, bool evidence = true) =>
            new()
            {
                SiteCollectionId = row.SiteCollectionId!.Value,
                FileUniqueId = row.FileUniqueId!.Value,
                AspxEvidenceJson = evidence
                    ? JsonSerializer.Serialize(
                        AspxPageEnricher.SourceNotRequested(100, false, view, form, "Complete", DateTime.UnixEpoch))
                    : null,
            };

        HashSet<(Guid SiteCollectionId, Guid FileUniqueId)> registered =
            ClassicContentCollectionModule.ListViewsAndForms(
                new WebFileInventory(
                    ClassicContentFixture.Site,
                    "/",
                    rows,
                    [Facts(withoutItem, false, true), Facts(withItem, true, false), Facts(rows[2], false, false)]));
        Assert.Equal(2, registered.Count);
        Assert.True(ClassicContentCollectionModule.IsListViewOrFormWithoutItem(withoutItem, registered));
        Assert.False(ClassicContentCollectionModule.IsListViewOrFormWithoutItem(withItem, registered));
        Assert.False(ClassicContentCollectionModule.IsListViewOrFormWithoutItem(withoutItem, []));
        Assert.Empty(
            ClassicContentCollectionModule.ListViewsAndForms(
                new WebFileInventory(
                    ClassicContentFixture.Site,
                    "/",
                    rows,
                    [Facts(withoutItem, null, null), Facts(withItem, true, true, evidence: false)])));
        Assert.Empty(
            ClassicContentCollectionModule.ListViewsAndForms(
                new WebFileInventory(ClassicContentFixture.Site, "/", rows)));
    }

    /// <summary>
    /// Verifies that a registered list form without an item finishes the assessment without a second read.
    /// </summary>
    [Fact]
    public async Task A_registered_list_form_without_an_item_finishes_the_assessment_without_a_second_read()
    {
        using StoreCase? data = new StoreCase("classicpage-list-form-unread");
        ClassicContentFixture? source = new ClassicContentFixture
        {
            IncludePhysicalFacts = true,
            TransformInventory = inventory =>
            {
                DiscoveryEvidence candidate = inventory.Evidence.Single(row => row.RecordKey == "page:6");
                PhysicalFileFacts form =
                    inventory.Files!.Single(facts => facts.FileUniqueId == candidate.FileUniqueId);
                form.AspxEvidenceJson = JsonSerializer.Serialize(
                    AspxPageEnricher.SourceNotRequested(100, false, false, true, "Complete", DateTime.UnixEpoch));
                return inventory;
            },
        };
        PipelineCoordinator? coordinator = new PipelineCoordinator(
            data.Store,
            ClassicPageModule.Registry(source.OpenAsync),
            new ForbiddenOnlineEnvironment());
        PhaseReply? ticket =
            await coordinator.StartPipelineAsync(new() { Collection = ClassicContentFixture.Request() });
        Guid id = Guid.Parse(ticket.AssessmentId);
        ScanStatus status = await coordinator.WaitForCompletionAsync(id);
        Assert.True(status == ScanStatus.Finished, (await data.Store.LatestRootAsync(id))!.LastError);
        Assert.DoesNotContain("page:6", source.Calls.Keys);
        Assert.Contains("page:5", source.Calls.Keys);
        using ScanContext? db = data.Store.CreateContext(id);
        Core.Storage.Pipeline.SourceObservationRow receipt = Assert.Single(
            await db.SourceObservations.AsNoTracking().ToListAsync(),
            row => row.SourceIdentity.StartsWith("FileEnrichment:", StringComparison.Ordinal) &&
                   row.SourceIdentity.EndsWith("|page:6", StringComparison.Ordinal));
        FilePageEvidence saved = CollectionJson.Read<FilePageEvidence>(receipt.RawBytes);
        Assert.Equal(AcquisitionStatus.Complete, receipt.AcquisitionStatus);
        Assert.Null(receipt.AcquisitionError);
        Assert.True(saved.FileLevelOnly);
        Assert.Equal("NotRequested", saved.WebPartsState.Status);
        Assert.Empty(saved.WebParts);
        JsonElement output = Assert.Single(
            (await db.AnalysisResults.AsNoTracking().ToListAsync())
            .Select(row => new VersionedJson(row.PayloadJson).Value)
            .Where(value => value.GetProperty("kind").GetString() == "FileEnrichment")
            .Select(value => value.GetProperty("output")),
            value => value.GetProperty("Discovery").ValueKind == JsonValueKind.Object &&
                     value.GetProperty("Discovery").GetProperty("RecordKey").GetString() == "page:6");
        Assert.Equal(JsonValueKind.Null, output.GetProperty("Page").ValueKind);
        Assert.Equal("NotApplicable", output.GetProperty("Discovery").GetProperty("AssessmentStatus").GetString());
        Assert.Equal(
            FileLevelPageProjection.RegisteredListViewOrFormCode,
            output.GetProperty("Discovery").GetProperty("ErrorCodes").GetString());
    }

    /// <summary>
    /// Verifies that a web connection waits for one reader at a time and the longest waiting one closes first.
    /// </summary>
    [Fact]
    public void A_web_connection_waits_for_one_reader_at_a_time_and_the_longest_waiting_one_closes_first()
    {
        WebSessionPool<TrackedSession> pool = new(2);
        TrackedSession first = new();
        TrackedSession second = new();
        TrackedSession third = new();
        TrackedSession repeated = new();
        Assert.False(pool.TryTake("first", out _));
        pool.Return("first", first);
        Assert.True(pool.TryTake("first", out TrackedSession? taken));
        Assert.Same(first, taken);
        Assert.False(pool.TryTake("first", out _));
        Assert.Equal(0, pool.Count);

        pool.Return("first", first);
        pool.Return("first", repeated);
        Assert.True(repeated.Closed);
        Assert.False(first.Closed);
        pool.Return("second", second);
        pool.Return("third", third);
        Assert.True(first.Closed);
        Assert.False(second.Closed);
        Assert.False(third.Closed);
        Assert.Equal(2, pool.Count);

        pool.Dispose();
        Assert.True(second.Closed);
        Assert.True(third.Closed);
        Assert.Equal(0, pool.Count);
        TrackedSession late = new();
        pool.Return("late", late);
        Assert.True(late.Closed);
        Assert.Equal(0, pool.Count);
    }

    /// <summary>
    /// Verifies that an answer from a kept connection is returned only when accepted and is otherwise read again.
    /// </summary>
    [Fact]
    public async Task An_answer_from_a_kept_connection_is_returned_only_when_accepted_and_is_otherwise_read_again()
    {
        WebSessionPool<TrackedSession> pool = new(2);
        List<TrackedSession> opened = [];
        List<TrackedSession> used = [];
        Queue<string> answers = new();

        Task<string> Read(CancellationToken token = default, bool usable = true) =>
            pool.ReadAsync(
                "web",
                _ => usable,
                () =>
                {
                    opened.Add(new TrackedSession());
                    return Task.FromResult(opened[^1]);
                },
                session =>
                {
                    used.Add(session);
                    string answer = answers.Dequeue();
                    return answer == "throw"
                        ? Task.FromException<string>(new InvalidOperationException("recorded failure"))
                        : Task.FromResult(answer);
                },
                answer => answer != "not returned",
                answer => answer == "clean",
                token);

        // A clean answer keeps the connection, and the next read uses it.
        answers.Enqueue("clean");
        answers.Enqueue("clean");
        Assert.Equal("clean", await Read());
        Assert.Equal("clean", await Read());
        Assert.Single(opened);
        Assert.Equal(2, used.Count);
        Assert.All(used, session => Assert.Same(opened[0], session));
        Assert.False(opened[0].Closed);

        // An answer the caller rejects on the kept connection is read again on a new connection.
        answers.Enqueue("not returned");
        answers.Enqueue("clean");
        Assert.Equal("clean", await Read());
        Assert.Equal(2, opened.Count);
        Assert.True(opened[0].Closed);
        Assert.Same(opened[1], used[^1]);
        Assert.False(opened[1].Closed);

        // A failure on the kept connection is read again, and the new connection's answer is the outcome.
        answers.Enqueue("throw");
        answers.Enqueue("not returned");
        Assert.Equal("not returned", await Read());
        Assert.Equal(3, opened.Count);
        Assert.True(opened[1].Closed);
        Assert.True(opened[2].Closed);
        Assert.Equal(0, pool.Count);

        // A failure on a new connection, with no earlier answer, is the outcome and closes that connection.
        answers.Enqueue("throw");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Read());
        Assert.Equal(4, opened.Count);
        Assert.True(opened[3].Closed);
        Assert.Equal(0, pool.Count);

        // An accepted answer that is not reusable is returned without a second read, and its connection closes.
        answers.Enqueue("clean");
        answers.Enqueue("layout failed");
        Assert.Equal("clean", await Read());
        Assert.Equal("layout failed", await Read());
        Assert.Equal(5, opened.Count);
        Assert.True(opened[4].Closed);
        Assert.Equal(0, pool.Count);

        // A rejected answer stands when the read on the new connection fails.
        answers.Enqueue("clean");
        answers.Enqueue("not returned");
        answers.Enqueue("throw");
        Assert.Equal("clean", await Read());
        Assert.Equal("not returned", await Read());
        Assert.Equal(7, opened.Count);
        Assert.True(opened[5].Closed);
        Assert.True(opened[6].Closed);
        Assert.Equal(0, pool.Count);

        // A cancelled run stops at the kept connection instead of reading again.
        using CancellationTokenSource cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        answers.Enqueue("clean");
        answers.Enqueue("throw");
        Assert.Equal("clean", await Read());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Read(cancelled.Token));
        Assert.Equal(8, opened.Count);
        Assert.True(opened[7].Closed);
        answers.Enqueue("clean");
        answers.Enqueue("not returned");
        Assert.Equal("clean", await Read());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Read(cancelled.Token));
        Assert.Equal(9, opened.Count);
        Assert.True(opened[8].Closed);
        Assert.Empty(answers);

        // A kept connection that is no longer usable is closed without being read.
        answers.Enqueue("clean");
        answers.Enqueue("clean");
        Assert.Equal("clean", await Read());
        Assert.Equal("clean", await Read(usable: false));
        Assert.Equal(11, opened.Count);
        Assert.True(opened[9].Closed);
        Assert.Same(opened[10], used[^1]);
        Assert.False(opened[10].Closed);
        pool.Dispose();
        Assert.True(opened[10].Closed);
    }

    /// <summary>
    /// Notes whether a pooled connection was closed.
    /// </summary>
    private sealed class TrackedSession : IDisposable
    {
        internal bool Closed { get; private set; }

        /// <summary>
        /// Marks the connection closed.
        /// </summary>
        public void Dispose() => Closed = true;
    }

    /// <summary>
    /// Verifies that a web connection is kept only after a read without a failure or denial.
    /// </summary>
    [Fact]
    public void A_web_connection_is_kept_only_after_a_read_without_a_failure_or_denial()
    {
        ClassicContentFixture fixture = new();
        DiscoveryEvidence candidate = fixture.DiscoveryRows()[5];
        FilePageEvidence unread = FilePageSourceReader.UnavailableFileEnrichment(
            fixture.Web(),
            candidate,
            SourceReadState.NotAttempted);
        Assert.True(ClassicContentReader.IsReusableAfter(unread));
        Assert.True(ClassicContentReader.IsReusableAfter(unread with { WebPartsState = SourceReadState.Complete }));
        Assert.True(
            ClassicContentReader.IsReusableAfter(
                FilePageSourceReader.ListViewOrFormEnrichment(fixture.Web(), candidate)));
        Assert.False(ClassicContentReader.IsReusableAfter(unread with { MetadataState = new("NotReturned", "none") }));
        Assert.False(ClassicContentReader.IsReusableAfter(unread with { MetadataState = new("Denied", "403") }));
        Assert.False(ClassicContentReader.IsReusableAfter(unread with { WebPartsState = new("Failed", "500") }));
        Assert.Equal(
            AcquisitionStatus.Complete,
            ClassicContentCollectionModule.FileEnrichmentStatus(
                FilePageSourceReader.ListViewOrFormEnrichment(fixture.Web(), candidate)));
        Assert.Equal(AcquisitionStatus.NotAttempted, ClassicContentCollectionModule.FileEnrichmentStatus(unread));
        Assert.False(ClassicContentReader.IsReusableAfter(unread with { HomeFallbackState = new("Failed", "500") }));
        FilePageEvidence layoutFailed =
            unread with { PublishingLayout = new("/layout.aspx", null, null, new("Failed", "500")) };
        Assert.False(ClassicContentReader.IsReusableAfter(layoutFailed));
        Assert.True(ClassicContentReader.IsAcceptedFromKeptConnection(layoutFailed));
        Assert.True(ClassicContentReader.IsAcceptedFromKeptConnection(unread));
        Assert.False(
            ClassicContentReader.IsAcceptedFromKeptConnection(
                unread with { MetadataState = new("NotReturned", "none") }));
        Assert.False(
            ClassicContentReader.IsAcceptedFromKeptConnection(unread with { WebPartsState = new("Denied", "403") }));
    }

    /// <summary>
    /// Verifies that source reader bounds content and observes cancellation.
    /// </summary>
    [Fact]
    public async Task Source_reader_bounds_content_and_observes_cancellation()
    {
        MemoryStream? large = new MemoryStream(new byte[AspxSourceParser.MaximumSourceBytes + 1]);
        AspxSourceObservation? source = await AspxSourceReader.ReadStreamAsync(large, default);
        Assert.Equal("TooLarge", source.State);
        Assert.Null(source.Bytes);
        using CancellationTokenSource? canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AspxSourceReader.ReadStreamAsync(new MemoryStream([1]), canceled.Token));
    }

    /// <summary>
    /// Verifies that taxonomy requires exact handlers and independent native role evidence.
    /// </summary>
    [Theory]
    [InlineData(null, false, false, false, null, "Unknown", "Unknown")]
    [InlineData("Unmapped.Handler", false, false, false, null, "Unknown", "Unknown")]
    [InlineData(
        "Microsoft.SharePoint.Mobile.WebControls.SPMobilePage",
        false,
        false,
        false,
        null,
        "Unknown",
        "Unknown")]
    [InlineData(
        "Microsoft.SharePoint.MobileControls.SPMobilePage",
        false,
        false,
        false,
        null,
        "deprecated",
        "Inapplicable")]
    [InlineData(null, true, false, false, null, "List", "Inapplicable")]
    [InlineData("Unmapped.Handler", false, true, false, null, "List", "Inapplicable")]
    [InlineData(null, false, false, true, "_catalogs/masterpage/custom.aspx", "catalog_masterpage", "Classic")]
    [InlineData("Unmapped.Handler", false, false, true, "_catalogs/other/custom.aspx", "catalog_other", "Classic")]
    [InlineData(
        "Microsoft.SharePoint.WebControls.ClientSidePage",
        false,
        false,
        true,
        "_catalogs/masterpage/custom.aspx",
        "catalog_masterpage",
        "Modern")]
    [InlineData("System.Web.UI.Page", false, false, false, null, "User", "Classic")]
    public void Taxonomy_requires_exact_handlers_and_independent_native_role_evidence(
        string? handler,
        bool view,
        bool form,
        bool catalog,
        string? path,
        string family,
        string experience)
    {
        AspxPageClassification? result = AspxPageClassifier.Classify(handler, null, view, form, catalog, path);
        Assert.Equal(family, result.ProductFamily);
        Assert.Equal(experience, result.Experience);
        if (catalog && handler is null or "Unmapped.Handler") Assert.Contains("ClassicDefaultAssumed", result.Reason);
        if (family != "User") Assert.NotEqual("Included", result.ImpactStatus);
        AspxPageClassification? incomplete = AspxPageClassifier.Classify(
            "System.Web.UI.Page",
            null,
            null,
            false,
            false);
        Assert.Equal("Unknown", incomplete.ProductFamily);
        Assert.Equal("Unknown", incomplete.Experience);
    }
}