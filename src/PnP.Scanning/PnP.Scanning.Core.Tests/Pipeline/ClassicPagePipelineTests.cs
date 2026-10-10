#nullable enable
using System.Globalization;
using System.Text.Json;
using CsvHelper;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Pipeline.Analysis.Module;
using PnP.Scanning.Core.Pipeline.Collection.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Pipeline.Orchestration;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Storage.Pipeline;
using Xunit;

namespace PnP.Scanning.Core.Tests.Pipeline;

[Trait("Category", "ClassicPagePipeline")]
[Collection("Native pipeline CLI")]
public sealed class ClassicPagePipelineTests
{
    [Fact]
    public async Task Native_collect_analyze_and_run_selected_report_use_the_production_page_module()
    {
        using var data = new StoreCase("classicpage-native-stages"); var fixture = new ClassicPageFixture(); var environment = new ForbiddenOnlineEnvironment();
        await using var host = await NativePipelineHost.StartAsync(data.Store, ClassicPageModule.Registry(fixture.OpenAsync), environment);
        var collected = await host.InvokeAsync("collect", "--mode", "classicpage", "--tenant", "contoso.sharepoint.com", "--applicationid", ClassicPageFixture.App,
            "--exportwebpartproperties", "--skipusageinformation", "--skipuserinformation");
        Assert.True(collected.ExitCode == 0, collected.Error); var id = Guid.Parse(collected.Ticket!.AssessmentId);
        Assert.Equal(ScanStatus.Finished, await host.Coordinator!.WaitForCompletionAsync(id)); environment.ForbidRestore = true;
        var analyzed = await host.InvokeAsync("analyze", "--id", collected.Ticket.AssessmentId, "--snapshot-id", collected.Ticket.SnapshotId, "--threads", "1");
        Assert.True(analyzed.ExitCode == 0, analyzed.Error); Assert.Equal(ScanStatus.Finished, await host.Coordinator.WaitForCompletionAsync(id));
        var path = System.IO.Path.Combine(data.DirectoryPath, "native-report");
        var reported = await host.InvokeAsync("report", "--id", collected.Ticket.AssessmentId, "--analysis-run-id", analyzed.Ticket!.AnalysisRunId,
            "--mode", "CsvOnly", "--path", path, "--open", "false");
        Assert.True(reported.ExitCode == 0, reported.Error + reported.Output);
        Assert.True(File.Exists(System.IO.Path.Combine(path, "analysis-run.json")));
        using var db = data.Store.CreateContext(id);
        var pages = (await db.ClassicPageReportRows.Where(x => x.Kind == "classicpages").ToListAsync()).Select(x => new VersionedJson(x.PayloadJson).Value.Deserialize<ClassicPage>()!).ToArray();
        Assert.All(pages, x => Assert.Null(x.ModifiedBy));
        var parts = (await db.ClassicPageReportRows.Where(x => x.Kind == "classicpagewebparts").ToListAsync()).Select(x => new VersionedJson(x.PayloadJson).Value.Deserialize<ClassicPageWebPart>()!).ToArray();
        Assert.Contains(parts, x => x.WebPartProperties != null);
        Assert.Contains(await db.Properties.ToListAsync(), x => x.Name == "Pages" && x.Value == "True");
        Assert.Equal(1, environment.RestoreRequests); Assert.Equal(0, host.Counter.Calls);
        var notReady = await host.InvokeAsync("report", "--id", collected.Ticket.AssessmentId, "--analysis-run-id", Guid.NewGuid().ToString(), "--mode", "CsvOnly", "--open", "false");
        Assert.Equal(1, notReady.ExitCode);
    }
    [Fact]
    public async Task Native_start_mode_classicpage_runs_real_collection_analysis_and_report_without_legacy_services()
    {
        using var data = new StoreCase("classicpage-native");
        var fixture = new ClassicPageFixture(); var environment = new ForbiddenOnlineEnvironment();
        await using var host = await NativePipelineHost.StartAsync(data.Store, ClassicPageModule.Registry(fixture.OpenAsync), environment);
        var started = await host.InvokeAsync("start", "--mode", "classicpage", "--applicationid", ClassicPageFixture.App,
            "--tenant", "contoso.sharepoint.com", "--threads", "2", "--skipusageinformation");
        Assert.True(started.ExitCode == 0, started.Error + started.Output); Assert.NotNull(started.Ticket);
        var id = Guid.Parse(started.Ticket.AssessmentId); var runId = Guid.Parse(started.Ticket.AnalysisRunId);
        Assert.Equal(ScanStatus.Finished, await host.Coordinator!.WaitForCompletionAsync(id));
        using var db = data.Store.CreateContext(id);
        var snapshot = await db.SourceSnapshots.SingleAsync(); Assert.True(snapshot.IsSealed); Assert.Equal("classicpage", snapshot.ModuleKey);
        var runs = await db.PhaseRuns.ToListAsync(); Assert.Equal(3, runs.Count);
        Assert.Equal("classicpage-v1", (await db.AnalysisRuns.SingleAsync()).RuleVersion);
        var pinned = new VersionedJson((await db.AnalysisRuns.SingleAsync()).ParametersJson);
        Assert.Equal(ClassicPageRuleMetadata.MappingDigest, pinned.Value.GetProperty("mappingDigest").GetString());
        Assert.Equal(4, await db.ClassicPageReportRows.CountAsync(x => x.Kind == "classicpages"));
        Assert.Equal(0, await db.ClassicPages.CountAsync()); Assert.Equal(0, await db.ClassicPageDiscoveries.CountAsync());
        var path = System.IO.Path.Combine(data.DirectoryPath, "report");
        using var report = host.Client.Report(new ReportRequest { Id = id.ToString(), AnalysisRunId = runId.ToString(), Mode = ReportMode.CsvOnly.ToString(), Path = path, Delimiter = "," });
        var messages = new List<ReportStatus>(); while (await report.ResponseStream.MoveNext(default)) messages.Add(report.ResponseStream.Current);
        Assert.DoesNotContain(messages, x => x.Type == Constants.MessageError);
        using var reader = new StreamReader(System.IO.Path.Combine(path, "classicpages.csv"));
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
        var pages = csv.GetRecords<ClassicPage>().ToArray();
        Assert.Equal(new[] { "ASPXPage", "PublishingPage", "WebPartPage", "WikiPage" }, pages.Select(x => x.PageType).Order().ToArray());
        Assert.True(pages.Single(x => x.PageType == "WikiPage").UncustomizedHomePage);
        Assert.Equal("author@contoso.com", pages[0].ModifiedBy);
        Assert.False(File.Exists(System.IO.Path.Combine(path, "classicpageauditusage.csv")));
        Assert.Equal(0, environment.OnlineRequests); Assert.Equal(0, environment.ProtectionRequests); Assert.Equal(0, host.Counter.Calls);
        Assert.Equal(1, fixture.Calls["sites"]); Assert.Equal(1, fixture.Calls["metadata"]);
        var listed = await host.Client.ListAsync(new ListRequest()); var listing = Assert.Single(listed.Status);
        Assert.Equal("pipeline", listing.ExecutionPath); Assert.Equal(0, listing.SiteCollectionsScanned);
    }

    [Fact]
    public async Task Collect_reopen_reanalyze_and_select_report_keep_original_bytes_and_results()
    {
        using var data = new StoreCase("classicpage-reanalysis"); var fixture = new ClassicPageFixture();
        var environment = new ForbiddenOnlineEnvironment();
        var coordinator = new PipelineCoordinator(data.Store, ClassicPageModule.Registry(fixture.OpenAsync), environment);
        var collected = await coordinator.CollectAsync(ClassicPageFixture.Request()); var id = Guid.Parse(collected.AssessmentId);
        Assert.Equal(ScanStatus.Finished, await coordinator.WaitForCompletionAsync(id));
        using var db = data.Store.CreateContext(id);
        Assert.Equal(0, await db.AnalysisResults.CountAsync());
        var before = await db.SourceObservations.AsNoTracking().OrderBy(x => x.ObservationId).Select(x => new { x.ObservationId, x.Sha256, x.Length }).ToArrayAsync();
        fixture.Revision = "later"; environment.ForbidRestore = true;
        var reopened = new PipelineStore(data.DirectoryPath);
        var offline = new PipelineCoordinator(reopened, ClassicPageModule.Registry((_, _) => throw new InvalidOperationException("Online source must not be opened")), environment);
        await offline.StartAsync(default);
        async Task<PhaseReply> Analyze()
        {
            var ticket = await offline.AnalyzeAsync(new AnalyzeRequest { Id = collected.AssessmentId, SnapshotId = collected.SnapshotId });
            Assert.Equal(ScanStatus.Finished, await offline.WaitForCompletionAsync(id)); return ticket;
        }
        var first = await Analyze(); var second = await Analyze(); Assert.NotEqual(first.AnalysisRunId, second.AnalysisRunId);
        var count = await db.SourceObservations.CountAsync(); Assert.Equal(count * 2, await db.AnalysisResults.CountAsync());
        var after = await db.SourceObservations.AsNoTracking().OrderBy(x => x.ObservationId).Select(x => new { x.ObservationId, x.Sha256, x.Length }).ToArrayAsync();
        Assert.Equal(before, after); Assert.Equal(1, fixture.Calls["sites"]);
        var a = await ClassicPageReportExporter.ExportAsync(reopened, id, Guid.Parse(first.AnalysisRunId), System.IO.Path.Combine(data.DirectoryPath, "a"), ",", false, default);
        var b = await ClassicPageReportExporter.ExportAsync(reopened, id, Guid.Parse(second.AnalysisRunId), System.IO.Path.Combine(data.DirectoryPath, "b"), ",", false, default);
        Assert.Equal(File.ReadAllBytes(System.IO.Path.Combine(a.Path, "classicpages.csv")), File.ReadAllBytes(System.IO.Path.Combine(b.Path, "classicpages.csv")));
        Assert.DoesNotContain("later", File.ReadAllText(System.IO.Path.Combine(a.Path, "classicpages.csv")));
        Assert.Equal(0, environment.OnlineRequests); Assert.Equal(1, environment.RestoreRequests);
        var latest = await ClassicPageReportExporter.ExportAsync(reopened, id, null, null, ",", false, default);
        Assert.Equal(Guid.Parse(second.AnalysisRunId), latest.RunId);
    }

    [Theory]
    [InlineData("page:3")]
    [InlineData("discovery")]
    public async Task Pause_and_reopen_collection_replay_receipts_and_continue_original_run(string blockAt)
    {
        using var data = new StoreCase("classicpage-resume"); var fixture = new ClassicPageFixture { BlockAt = blockAt };
        var environment = new ForbiddenOnlineEnvironment(); PhaseReply ticket;
        await using (var host = await NativePipelineHost.StartAsync(data.Store, ClassicPageModule.Registry(fixture.OpenAsync), environment))
        {
            ticket = (await host.InvokeAsync("start", "--mode", "classicpage", "--applicationid", ClassicPageFixture.App, "--tenant", "contoso.sharepoint.com", "--threads", "1", "--skipusageinformation")).Ticket!;
            await fixture.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(0, (await host.InvokeAsync("pause", "--id", ticket.AssessmentId)).ExitCode);
            using var db = data.Store.CreateContext(Guid.Parse(ticket.AssessmentId));
            Assert.False((await db.SourceSnapshots.SingleAsync()).IsSealed);
            Assert.Equal(0, await db.AnalysisRuns.CountAsync());
        }
        fixture.BlockAt = null;
        await using (var host = await NativePipelineHost.StartAsync(new PipelineStore(data.DirectoryPath), ClassicPageModule.Registry(fixture.OpenAsync), environment))
        {
            Assert.Equal(0, (await host.InvokeAsync("restart", "--id", ticket.AssessmentId)).ExitCode);
            Assert.Equal(ScanStatus.Finished, await host.Coordinator!.WaitForCompletionAsync(Guid.Parse(ticket.AssessmentId)));
        }
        Assert.Equal(1, fixture.Calls["sites"]); Assert.Equal(1, fixture.Calls["webs"]); Assert.Equal(1, fixture.Calls["identity"]);
        if (blockAt != "discovery") { Assert.Equal(1, fixture.Calls["page:1"]); Assert.Equal(1, fixture.Calls["page:2"]); }
        using var read = data.Store.CreateContext(Guid.Parse(ticket.AssessmentId));
        Assert.Equal(Guid.Parse(ticket.AnalysisRunId), (await read.AnalysisRuns.SingleAsync()).AnalysisRunId);
        Assert.Equal(2, await read.SourceObservations.CountAsync(x => x.SourceIdentity.StartsWith("DiscoveryRequest:")));
    }

    [Fact]
    public async Task Discovery_survives_failed_metadata_and_analysis_reports_failures_without_invented_pages()
    {
        using var data = new StoreCase("classicpage-failure"); var fixture = new ClassicPageFixture { FailEnrichment = true };
        var coordinator = new PipelineCoordinator(data.Store, ClassicPageModule.Registry(fixture.OpenAsync), new ForbiddenOnlineEnvironment());
        var ticket = await coordinator.StartPipelineAsync(new StartPipelineRequest { Collection = ClassicPageFixture.Request() });
        var id = Guid.Parse(ticket.AssessmentId); Assert.Equal(ScanStatus.Finished, await coordinator.WaitForCompletionAsync(id));
        using var db = data.Store.CreateContext(id);
        var sources = await db.SourceObservations.Where(x => x.SourceIdentity.StartsWith("Page:")).ToListAsync(); Assert.Equal(7, sources.Count);
        Assert.Equal(0, await db.ClassicPageReportRows.CountAsync(x => x.Kind == "classicpages"));
        var rows = (await db.ClassicPageReportRows.Where(x => x.Kind == "discovery").ToListAsync())
            .Select(x => new VersionedJson(x.PayloadJson).Value.Deserialize<ClassicPageDiscovery>()!).ToArray();
        Assert.Equal(7, rows.Count(x => x.RowType == "Page")); Assert.Equal(6, rows.Count(x => x.AssessmentStatus == "Failed"));
        Assert.True((await data.Store.LatestRootAsync(id))!.ErrorCount > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Page_options_blog_and_audit_statistics_preserve_scope(bool homeOnly)
    {
        using var data = new StoreCase("classicpage-options"); var fixture = new ClassicPageFixture { IncludeBlog = true };
        var coordinator = new PipelineCoordinator(data.Store, ClassicPageModule.Registry(fixture.OpenAsync), new ForbiddenOnlineEnvironment());
        var ticket = await coordinator.StartPipelineAsync(new() { Collection = ClassicPageFixture.Request(usage: true, homeOnly: homeOnly) });
        var id = Guid.Parse(ticket.AssessmentId); Assert.Equal(ScanStatus.Finished, await coordinator.WaitForCompletionAsync(id));
        using var db = data.Store.CreateContext(id);
        var pages = (await db.ClassicPageReportRows.Where(x => x.Kind == "classicpages").ToListAsync()).Select(x => new VersionedJson(x.PayloadJson).Value.Deserialize<ClassicPage>()!).ToArray();
        Assert.Equal(homeOnly ? 1 : 6, pages.Length); Assert.Equal(homeOnly ? 0 : 2, pages.Count(x => x.PageType == "BlogPage"));
        var usage = Assert.Single(await db.ClassicPageReportRows.Where(x => x.Kind == "classicpageauditusage").ToListAsync());
        var projected = new VersionedJson(usage.PayloadJson).Value.Deserialize<ClassicPageAuditUsage>()!;
        Assert.Equal(1, projected.AuditViewsCount); Assert.Equal(1, projected.AuditEditsCount); Assert.Equal(1, projected.AuditUniqueUsers);
        Assert.Equal("succeeded", projected.QueryStatus); Assert.Equal(1, fixture.Calls["audit"]);
    }

    [Fact]
    public async Task Unsupported_component_and_rule_are_rejected_before_auth_and_database_creation()
    {
        using var data = new StoreCase("classicpage-preflight"); var environment = new ForbiddenOnlineEnvironment();
        var coordinator = new PipelineCoordinator(data.Store, ClassicPageModule.Registry(), environment);
        var request = ClassicPageFixture.Request(); request.CollectionOptions.Properties.Add(new PropertyRequest { Property = "InfoPath", Type = "bool", Value = "True" });
        await Assert.ThrowsAsync<ArgumentException>(() => coordinator.CollectAsync(request));
        await Assert.ThrowsAsync<NotSupportedException>(() => coordinator.StartPipelineAsync(new() { Collection = ClassicPageFixture.Request(), RuleVersion = "unavailable" }));
        Assert.Empty(data.Store.AssessmentsOnDisk()); Assert.Equal(0, environment.OnlineRequests); Assert.Equal(0, environment.ProtectionRequests);
        await using var host = await NativePipelineHost.StartAsync(data.Store, ClassicPageModule.Registry(), environment);
        Assert.NotEmpty(host.Parser.Parse(["start", "--mode", "classicpage", "--classicinclude", "InfoPath"]).Errors);
        Assert.NotEmpty(host.Parser.Parse(["start", "--mode", "classicpage", "--module", "other"]).Errors);
        Assert.Empty(host.Parser.Parse(["start", "--mode", "classic", "--tenant", "contoso.sharepoint.com", "--applicationid", ClassicPageFixture.App]).Errors);
    }
}
