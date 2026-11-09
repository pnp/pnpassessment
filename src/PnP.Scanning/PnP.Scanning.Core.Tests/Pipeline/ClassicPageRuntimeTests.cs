#nullable enable
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Pipeline.Analysis.Module;
using PnP.Scanning.Core.Pipeline.Analysis.Page;
using PnP.Scanning.Core.Pipeline.Collection.Module;
using PnP.Scanning.Core.Pipeline.Collection.Page;
using PnP.Scanning.Core.Pipeline.Collection.Shared;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.List;
using PnP.Scanning.Core.Pipeline.Contracts.Page;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Pipeline.Orchestration;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using Xunit;

namespace PnP.Scanning.Core.Tests.Pipeline;

[Trait("Category", "Pipeline")]
public sealed class ClassicPageRuntimeTests
{
    [Fact]
    public async Task Foreign_assessment_file_evidence_cannot_seal_or_start_analysis()
    {
        using var data = new StoreCase("classicpage-foreign-assessment");
        var source = new ClassicPageFixture { IncludePhysicalFacts = true, ExtraFileAssessment = Guid.NewGuid() };
        var coordinator = new PipelineCoordinator(data.Store, ClassicPageModule.Registry(source.OpenAsync), new ForbiddenOnlineEnvironment());
        var ticket = await coordinator.StartPipelineAsync(new() { Collection = ClassicPageFixture.Request() });
        var id = Guid.Parse(ticket.AssessmentId);
        Assert.Equal(ScanStatus.Terminated, await coordinator.WaitForCompletionAsync(id));
        using var db = data.Store.CreateContext(id);
        Assert.False((await db.SourceSnapshots.SingleAsync()).IsSealed);
        Assert.Empty(await db.AnalysisRuns.ToListAsync());
        Assert.Contains("different assessment", (await data.Store.LatestRootAsync(id))!.LastError);
    }

    [Fact]
    public async Task Each_csom_scope_disposes_its_transport_without_disposing_the_shared_sdk_client()
    {
        using var shared = new HttpClient(new RecordedHandler());
        var transport = new HttpClient(new CollectionHttpClient(shared, default));
        using (new ClassicPageOnlineSource.CsomLease(new Microsoft.SharePoint.Client.ClientContext(
            "https://contoso.invalid"), transport))
        {
            using var response = await transport.GetAsync("https://contoso.invalid");
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        }
        await Assert.ThrowsAsync<ObjectDisposedException>(() => transport.GetAsync("https://contoso.invalid"));
        using var after = await shared.GetAsync("https://contoso.invalid");
        Assert.Equal(System.Net.HttpStatusCode.OK, after.StatusCode);
    }

    private sealed class RecordedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }

    [Fact]
    public async Task Denied_discovery_is_not_an_authoritative_empty_result()
    {
        using var data = new StoreCase("classicpage-denied-discovery");
        var source = new ClassicPageFixture { DeniedDiscovery = true };
        var coordinator = new PipelineCoordinator(data.Store, ClassicPageModule.Registry(source.OpenAsync), new ForbiddenOnlineEnvironment());
        var ticket = await coordinator.StartPipelineAsync(new() { Collection = ClassicPageFixture.Request() });
        var id = Guid.Parse(ticket.AssessmentId);
        Assert.Equal(ScanStatus.Finished, await coordinator.WaitForCompletionAsync(id));
        using var db = data.Store.CreateContext(id);
        var result = (await db.AnalysisResults.ToListAsync()).Single(row =>
            new VersionedJson(row.PayloadJson).Value.GetProperty("kind").GetString() == "Discovery");
        Assert.Equal(AnalysisOutcome.Unknown, result.Outcome);
        var value = new VersionedJson(result.PayloadJson).Value;
        Assert.Empty(value.GetProperty("files").EnumerateArray());
        Assert.Equal("Denied", value.GetProperty("coverage")[0].GetProperty("DiscoveryStatus").GetString());
    }

    [Fact]
    public async Task Denied_optional_web_metadata_retains_discovered_native_files()
    {
        using var data = new StoreCase("classicpage-partial-web");
        var source = new ClassicPageFixture { IncludePhysicalFacts = true, FailEnrichment = true };
        var coordinator = new PipelineCoordinator(data.Store, ClassicPageModule.Registry(source.OpenAsync), new ForbiddenOnlineEnvironment());
        var ticket = await coordinator.StartPipelineAsync(new() { Collection = ClassicPageFixture.Request() });
        var id = Guid.Parse(ticket.AssessmentId);
        Assert.Equal(ScanStatus.Finished, await coordinator.WaitForCompletionAsync(id));
        using var db = data.Store.CreateContext(id);
        Assert.All(await db.AnalysisRuns.AsNoTracking().ToListAsync(), run =>
        {
            Assert.NotNull(run.ReportDigest);
            Assert.NotNull(run.ReportPublishedAtUtc);
        });
        var discovery = await db.SourceObservations.SingleAsync(x => x.SourceIdentity.StartsWith("Discovery:"));
        Assert.Equal(8, ClassicPageSourceJson.Read<ClassicPageDiscoverySource>(discovery.RawBytes).Files!.Length);
        Assert.True(await db.AnalysisResults.AnyAsync(x => x.Outcome == AnalysisOutcome.Unknown));
        Assert.DoesNotContain(source.Calls.Keys, key => key.StartsWith("page:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Generic_safety_errors_are_not_misclassified_as_the_specific_form_identity_failure()
    {
        Assert.False(AspxPageEnricher.IsNullGuidServerError("Microsoft.SharePoint.SPException",
            "The type or namespace is not allowed by SafeControls."));
        Assert.False(AspxPageEnricher.IsNullGuidServerError("System.ArgumentNullException", "Parameter name: other"));
        Assert.True(AspxPageEnricher.IsNullGuidServerError("System.ArgumentNullException", "Value cannot be null.\r\nParameter name: g"));
        var calls = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => AspxPageEnricher.ReadWithFormIdentityFallbackAsync(false,
            Guid.NewGuid(), _ => { calls++; throw new InvalidDataException("SafeControls rejected the request."); }, default));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Typed_fields_preserve_null_and_nested_values_and_reject_unknown_types()
    {
        Assert.Null(SourceField.Of("Null", null).ToValue());
        Assert.Equal(Guid.Empty, SourceField.Of("Guid", Guid.Empty).ToValue());
        Assert.Equal(long.MaxValue, SourceField.Of("Int64", long.MaxValue).ToValue());
        var value = (JsonElement)SourceField.Of("Json", new { original = new[] { "one", "two" } }).ToValue()!;
        Assert.Equal(2, value.GetProperty("original").GetArrayLength());
        Assert.Throws<InvalidDataException>(() => SourceField.Of("Unmapped", 1).ToValue());
    }

    [Fact]
    public async Task Production_module_replays_native_files_actual_blog_items_and_audit_without_online_access()
    {
        using var data = new StoreCase("classicpage-replay");
        var source = new ClassicPageFixture
        {
            IncludePhysicalFacts = true, IncludeBlog = true, IncludeSecondBlog = true,
            PostsOnNonBlogWeb = true, IncludeDelve = true,
        };
        var environment = new ForbiddenOnlineEnvironment();
        var coordinator = new PipelineCoordinator(data.Store, ClassicPageModule.Registry(source.OpenAsync), environment);
        var ticket = await coordinator.StartPipelineAsync(new() { Collection = ClassicPageFixture.Request(usage: true) });
        var id = Guid.Parse(ticket.AssessmentId);
        Assert.Equal(ScanStatus.Finished, await coordinator.WaitForCompletionAsync(id));
        using var db = data.Store.CreateContext(id);
        var original = await db.AnalysisResults.AsNoTracking().ToDictionaryAsync(x => x.ObservationId, x => x.PayloadJson);
        var observations = await db.SourceObservations.AsNoTracking().ToListAsync();
        var blogs = observations.Where(x => new VersionedJson(x.MetadataJson).Value.GetProperty("kind").GetString() == "Blog")
            .Select(x => ClassicPageSourceJson.Read<ClassicPageBlogBatchSource>(x.RawBytes)).ToArray();
        Assert.Equal(5, blogs.Sum(x => x.Items.Length));
        Assert.Equal(3, blogs.Select(x => x.ListId).Distinct().Count());
        var pages = original.Values.Select(x => new VersionedJson(x).Value)
            .Where(x => x.GetProperty("kind").GetString() == "Blog")
            .SelectMany(x => x.GetProperty("pages").EnumerateArray()).ToArray();
        Assert.Contains(pages, page => page.GetProperty("PageUrl").GetString() == "/sites/a/Lists/Posts/Post.aspx?ID=100");
        Assert.Contains(pages, page => page.GetProperty("PageUrl").GetString() == "/sites/a/Stories/post.pointpub");
        var discovery = original.Values.Select(x => new VersionedJson(x).Value)
            .Single(x => x.GetProperty("kind").GetString() == "Discovery");
        Assert.Equal(8, discovery.GetProperty("files").GetArrayLength());
        Assert.All(discovery.GetProperty("files").EnumerateArray(), file =>
        {
            Assert.Equal(ClassicPageFixture.SiteId, file.GetProperty("SiteCollectionId").GetGuid());
            Assert.NotEqual(Guid.Empty, file.GetProperty("FileUniqueId").GetGuid());
        });
        Assert.Contains(await db.AnalysisResults.ToListAsync(), x => x.Outcome == AnalysisOutcome.Failed);
        var manifest = await data.Store.OpenSnapshotAsync(id, Guid.Parse(ticket.SnapshotId));
        var calls = source.Calls.ToDictionary(x => x.Key, x => x.Value);
        environment.ForbidRestore = true;
        var detached = new ModuleRegistry(analysis:
            [new(ClassicPageModule.Key, ClassicPageModule.RuleVersion, [ClassicPageModule.InputVersion],
                () => new ClassicPageAnalysisModule(), ClassicPageRuleMetadata.Validate, ClassicPageRuleMetadata.Pin)],
            current: new Dictionary<string, string> { [ClassicPageModule.Key] = ClassicPageModule.RuleVersion });
        var offline = new PipelineCoordinator(new(data.DirectoryPath), detached, environment);
        var replay = await offline.AnalyzeAsync(new() { Id = ticket.AssessmentId, SnapshotId = ticket.SnapshotId });
        Assert.Equal(ScanStatus.Finished, await offline.WaitForCompletionAsync(id));
        var replayId = Guid.Parse(replay.AnalysisRunId);
        var repeated = await db.AnalysisResults.AsNoTracking().Where(x => x.AnalysisRunId == replayId).ToListAsync();
        Assert.Equal(original.Count, repeated.Count);
        Assert.All(repeated, result => Assert.Equal(original[result.ObservationId], result.PayloadJson));
        Assert.Equal(manifest.ManifestDigest, (await data.Store.OpenSnapshotAsync(id, manifest.SnapshotId)).ManifestDigest);
        Assert.Equal(calls.OrderBy(x => x.Key), source.Calls.OrderBy(x => x.Key));
        Assert.Equal(0, environment.OnlineRequests);
        Assert.Equal(0, environment.ProtectionRequests);
    }

    [Fact]
    public async Task Production_collection_pause_reuses_committed_discovery_receipts()
    {
        using var data = new StoreCase("classicpage-resume");
        var source = new ClassicPageFixture { BlockAt = "discovery", IncludePhysicalFacts = true };
        var coordinator = new PipelineCoordinator(data.Store, ClassicPageModule.Registry(source.OpenAsync), new ForbiddenOnlineEnvironment());
        var ticket = await coordinator.CollectAsync(ClassicPageFixture.Request());
        var id = Guid.Parse(ticket.AssessmentId);
        await source.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await coordinator.PauseAsync(id, false);
        source.BlockAt = null;
        source.Revision = "later-response";
        var reopened = new PipelineCoordinator(new(data.DirectoryPath), ClassicPageModule.Registry(source.OpenAsync), new ForbiddenOnlineEnvironment());
        await reopened.RestartAsync(id);
        Assert.Equal(ScanStatus.Finished, await reopened.WaitForCompletionAsync(id));
        Assert.Equal(1, source.Calls["sites"]);
        Assert.Equal(1, source.Calls["webs"]);
        using var db = data.Store.CreateContext(id);
        var first = await db.SourceObservations.SingleAsync(x => x.SourceIdentity == "DiscoveryRequest:batch:0");
        Assert.Equal("original", new VersionedJson(Encoding.UTF8.GetString(first.RawBytes!)).Value.GetProperty("Revision").GetString());
        Assert.Single(await db.SourceSnapshots.ToListAsync());
    }

    [Fact]
    public async Task Original_source_bytes_survive_storage_and_drive_offline_classification()
    {
        var bytes = Encoding.Unicode.GetPreamble().Concat(
            Encoding.Unicode.GetBytes("<%@ Page Inherits=\"Microsoft.SharePoint.WebPartPages.WebPartPage\" %>")).ToArray();
        var acquired = await AspxSourceReader.ReadStreamAsync(new MemoryStream(bytes), default);
        Assert.Equal(bytes, acquired.Bytes);
        var file = new SPFileSource
        {
            SiteCollectionId = Guid.NewGuid(), FileUniqueId = Guid.NewGuid(), WebId = Guid.NewGuid(),
            SiteUrl = "https://contoso.sharepoint.com/sites/a", WebUrl = "/",
            ServerRelativeUrl = "/sites/a/_catalogs/masterpage/custom.aspx",
            AspxEvidenceJson = JsonSerializer.Serialize(new AspxPageEvidence("Available", null, null, "PendingAnalysis",
                null, null, 116, true, false, false, "Complete", DateTime.UnixEpoch, acquired.Bytes)),
        };
        using var data = new StoreCase("classicpage-bytes");
        var observation = new CollectedObservation(Guid.NewGuid(),
            $"file|{file.SiteCollectionId:D}|{file.FileUniqueId:D}", null, AcquisitionStatus.Complete,
            VersionedJson.From(new { kind = "PhysicalFile" }), ClassicPageSourceJson.Bytes(file));
        var seed = await data.UnsealedAsync([observation]);
        await seed.Writer.SealAsync(default);
        var saved = ClassicPageSourceJson.Read<SPFileSource>((await data.Store.SnapshotReader(seed.Assessment, seed.Snapshot)
            .ReadAsync(observation.ObservationId, default)).Artifact.GetBytes());
        Assert.Equal(bytes, JsonSerializer.Deserialize<AspxPageEvidence>(saved.AspxEvidenceJson!)!.OriginalBytes);
        var analysis = PhysicalFileAnalysisRules.Analyze(saved);
        Assert.Equal("catalog_masterpage", analysis.Classification!.ProductFamily);
        Assert.Equal("Classic", analysis.Classification.Experience);
        Assert.Equal("Excluded", analysis.Classification.ImpactStatus);
        Assert.Equal("Declared", analysis.HandlerResolution);
    }

    [Fact]
    public async Task Source_reader_bounds_content_and_observes_cancellation()
    {
        var large = new MemoryStream(new byte[AspxSourceParser.MaximumSourceBytes + 1]);
        var source = await AspxSourceReader.ReadStreamAsync(large, default);
        Assert.Equal("TooLarge", source.State);
        Assert.Null(source.Bytes);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AspxSourceReader.ReadStreamAsync(new MemoryStream([1]), canceled.Token));
    }

    [Theory]
    [InlineData(null, false, false, false, null, "Unknown", "Unknown")]
    [InlineData("Unmapped.Handler", false, false, false, null, "Unknown", "Unknown")]
    [InlineData("Microsoft.SharePoint.Mobile.WebControls.SPMobilePage", false, false, false, null, "Unknown", "Unknown")]
    [InlineData("Microsoft.SharePoint.MobileControls.SPMobilePage", false, false, false, null, "deprecated", "Inapplicable")]
    [InlineData(null, true, false, false, null, "List", "Inapplicable")]
    [InlineData("Unmapped.Handler", false, true, false, null, "List", "Inapplicable")]
    [InlineData(null, false, false, true, "_catalogs/masterpage/custom.aspx", "catalog_masterpage", "Classic")]
    [InlineData("Unmapped.Handler", false, false, true, "_catalogs/other/custom.aspx", "catalog_other", "Classic")]
    [InlineData("Microsoft.SharePoint.WebControls.ClientSidePage", false, false, true, "_catalogs/masterpage/custom.aspx", "catalog_masterpage", "Modern")]
    [InlineData("System.Web.UI.Page", false, false, false, null, "User", "Classic")]
    public void Taxonomy_requires_exact_handlers_and_independent_native_role_evidence(string? handler,
        bool view, bool form, bool catalog, string? path, string family, string experience)
    {
        var result = AspxPageClassifier.Classify(handler, null, view, form, catalog, path);
        Assert.Equal(family, result.ProductFamily);
        Assert.Equal(experience, result.Experience);
        if (catalog && handler is null or "Unmapped.Handler") Assert.Contains("ClassicDefaultAssumed", result.Reason);
        if (family != "User") Assert.NotEqual("Included", result.ImpactStatus);
        var incomplete = AspxPageClassifier.Classify("System.Web.UI.Page", null, null, false, false);
        Assert.Equal("Unknown", incomplete.ProductFamily);
        Assert.Equal("Unknown", incomplete.Experience);
    }
}
