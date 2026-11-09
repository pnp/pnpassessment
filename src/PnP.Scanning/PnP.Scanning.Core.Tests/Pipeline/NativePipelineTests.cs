#nullable enable
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Pipeline.Collection.Module;
using PnP.Scanning.Core.Pipeline.Orchestration;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Storage.Pipeline;
using Xunit;

namespace PnP.Scanning.Core.Tests.Pipeline;

[CollectionDefinition("Native pipeline CLI", DisableParallelization = true)]
public sealed class NativePipelineCollection;

[Collection("Native pipeline CLI")]
[Trait("Category", "Pipeline")]
public sealed class NativePipelineTests
{
    [Fact]
    public async Task Existing_report_rpc_exports_real_replacement_results_without_online_access_or_new_csv_contracts()
    {
        using var output = new StoreCase("classic-native-report");
        var store = new PipelineStore(AppContext.BaseDirectory);
        var source = new ClassicPageFixture { IncludePhysicalFacts = true, IncludeBlog = true, IncludeDelve = true };
        Guid assessment = Guid.Empty;
        try
        {
            await using var host = await NativePipelineHost.StartAsync(store, ClassicPageModule.Registry(source.OpenAsync), new());
            using (var call = host.Client.Start(ClassicPageFixture.MakeOptions(usage: true)))
                await foreach (var message in call.ResponseStream.ReadAllAsync())
                {
                    Assert.NotEqual(Constants.MessageError, message.Type);
                    var match = System.Text.RegularExpressions.Regex.Match(message.Status, @"Assessment id = ([0-9a-f-]{36})");
                    if (match.Success) assessment = Guid.Parse(match.Groups[1].Value);
                }
            Assert.NotEqual(Guid.Empty, assessment);
            Assert.Equal(ScanStatus.Finished, await host.Coordinator!.WaitForCompletionAsync(assessment));
            var calls = source.Calls.ToDictionary(x => x.Key, x => x.Value);
            for (var repeat = 0; repeat < 2; repeat++)
            {
                using var report = host.Client.Report(new ReportRequest
                {
                    Id = assessment.ToString(), Mode = ReportMode.CsvOnly.ToString(), Path = output.DirectoryPath, Delimiter = ",",
                });
                var messages = new List<ReportStatus>();
                await foreach (var message in report.ResponseStream.ReadAllAsync()) messages.Add(message);
                Assert.DoesNotContain(messages, x => x.Type == Constants.MessageError);
                Assert.Contains(messages, x => !string.IsNullOrEmpty(x.ReportPath));
            }
            Assert.Contains("Home.aspx", await File.ReadAllTextAsync(Path.Combine(output.DirectoryPath, "classicpages.csv")));
            Assert.Contains("BlogPage", await File.ReadAllTextAsync(Path.Combine(output.DirectoryPath, "classicpages.csv")));
            Assert.Contains("Classic", await File.ReadAllTextAsync(Path.Combine(output.DirectoryPath, "scans.csv")));
            using var db = store.CreateContext(assessment, readOnly: true);
            Assert.True(await db.ClassicPageAuditUsages.AnyAsync(x => x.AuditViewsCount == 1 && x.AuditEditsCount == 1));
            Assert.Equal(calls.OrderBy(x => x.Key), source.Calls.OrderBy(x => x.Key));
            Assert.Equal(0, host.Counter.Calls);
        }
        finally
        {
            if (assessment != Guid.Empty)
            {
                using var context = new ScanContext(assessment);
                Microsoft.Data.Sqlite.SqliteConnection.ClearPool(
                    (Microsoft.Data.Sqlite.SqliteConnection)context.Database.GetDbConnection());
                Directory.Delete(Path.Combine(AppContext.BaseDirectory, assessment.ToString()), true);
            }
        }
    }

    [Fact]
    public async Task Existing_restart_adopts_a_historical_classic_assessment_without_using_old_page_execution()
    {
        using var data = new StoreCase("classic-historical-restart");
        var assessment = Guid.NewGuid();
        Directory.CreateDirectory(Path.GetDirectoryName(data.Store.DatabasePath(assessment))!);
        using (var db = data.Store.CreateContext(assessment))
        {
            await db.Database.MigrateAsync();
            var request = ClassicPageFixture.MakeOptions();
            db.Scans.Add(new PnP.Scanning.Core.Storage.Scan
            {
                ScanId = assessment, CLIMode = "Classic", CLITenant = request.Tenant, CLITenantId = request.TenantId,
                CLIEnvironment = request.Environment, CLIAuthMode = request.AuthMode, CLIApplicationId = request.ApplicationId,
                CLIThreads = 1, Status = ScanStatus.Paused, CLISiteList = request.SitesList,
            });
            db.Properties.AddRange(request.Properties.Select(x => new Property
            {
                ScanId = assessment, Name = x.Property, Type = x.Type, Value = x.Value,
            }));
            await db.SaveChangesAsync();
        }
        var source = new ClassicPageFixture { IncludePhysicalFacts = true };
        await using var host = await NativePipelineHost.StartAsync(data.Store, ClassicPageModule.Registry(source.OpenAsync), new());
        using (var restart = host.Client.Restart(new RestartRequest { Id = assessment.ToString() }))
            await foreach (var message in restart.ResponseStream.ReadAllAsync())
                Assert.NotEqual(Constants.MessageError, message.Type);
        Assert.Equal(ScanStatus.Finished, await host.Coordinator!.WaitForCompletionAsync(assessment));
        using var saved = data.Store.CreateContext(assessment);
        Assert.Equal(assessment, (await saved.Scans.SingleAsync()).ScanId);
        Assert.Single(await saved.SourceSnapshots.ToListAsync());
        Assert.True(await saved.ClassicPages.AnyAsync());
        Assert.Equal(0, host.Counter.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Existing_start_command_routes_pages_and_mixed_components_through_the_new_pipeline(bool mixed)
    {
        using var data = new StoreCase("classic-existing-cli");
        var source = new ClassicPageFixture { IncludePhysicalFacts = true, IncludeBlog = true, IncludeDelve = true };
        await using var host = await NativePipelineHost.StartAsync(data.Store, ClassicPageModule.Registry(source.OpenAsync), new());
        var arguments = new List<string>
        {
            "start", "--mode", "Classic", "--classicinclude", "Pages",
            "--tenant", "contoso.sharepoint.com", "--tenantid", ClassicPageFixture.SiteId.ToString(),
            "--applicationid", ClassicPageFixture.App, "--authmode", "application",
            "--certpath", "My|CurrentUser|SYNTHETIC", "--skipusageinformation", "--threads", "1",
        };
        if (mixed) arguments.InsertRange(5, ["Lists", "InfoPath", "Extensibility"]);
        var result = await host.InvokeAsync(arguments.ToArray());
        Assert.Equal(0, result.ExitCode);
        await StoreCase.WaitUntilAsync(() => Task.FromResult(data.Store.AssessmentsOnDisk().Any()));
        var assessment = Assert.Single(data.Store.AssessmentsOnDisk());
        Assert.Equal(ScanStatus.Finished, await host.Coordinator!.WaitForCompletionAsync(assessment));
        Assert.Equal(0, host.Counter.Calls);
        Assert.True(source.Calls.ContainsKey("blog-lists"));
        Assert.Equal(mixed, source.Calls.ContainsKey("components"));
        using var db = data.Store.CreateContext(assessment);
        Assert.Equal("Classic", (await db.Scans.SingleAsync()).CLIMode);
        Assert.True(await db.ClassicPages.AnyAsync(x => x.PageType == "BlogPage"));
        Assert.True(await db.ClassicPages.AnyAsync(x => x.PageType == "DelveBlogPage"));
        Assert.Equal(mixed ? 1 : 0, await db.ClassicLists.CountAsync());
        Assert.Single(await db.SourceSnapshots.ToListAsync());
        Assert.Single(await db.AnalysisRuns.ToListAsync());
    }

    [Fact]
    public async Task Default_all_classic_selection_uses_the_same_replacement()
    {
        using var data = new StoreCase("classic-default-all");
        var source = new ClassicPageFixture { IncludePhysicalFacts = true };
        await using var host = await NativePipelineHost.StartAsync(data.Store, ClassicPageModule.Registry(source.OpenAsync), new());
        var result = await host.InvokeAsync("start", "--mode", "Classic", "--tenant", "contoso.sharepoint.com",
            "--tenantid", ClassicPageFixture.SiteId.ToString(), "--applicationid", ClassicPageFixture.App,
            "--authmode", "application", "--certpath", "My|CurrentUser|SYNTHETIC", "--skipusageinformation");
        Assert.Equal(0, result.ExitCode);
        await StoreCase.WaitUntilAsync(() => Task.FromResult(data.Store.AssessmentsOnDisk().Any()));
        var id = Assert.Single(data.Store.AssessmentsOnDisk());
        Assert.Equal(ScanStatus.Finished, await host.Coordinator!.WaitForCompletionAsync(id));
        Assert.Equal(1, source.Calls["components"]);
        Assert.Equal(0, host.Counter.Calls);
    }

    [Fact]
    public async Task Existing_native_Start_pause_restart_and_list_preserve_phase_identity()
    {
        using var data = new StoreCase("classic-native-restart");
        var source = new ClassicPageFixture { BlockAt = "discovery", IncludePhysicalFacts = true };
        var environment = new ForbiddenOnlineEnvironment();
        await using var host = await NativePipelineHost.StartAsync(data.Store, ClassicPageModule.Registry(source.OpenAsync), environment);
        using (var call = host.Client.Start(ClassicPageFixture.MakeOptions()))
        {
            var messages = new List<StartStatus>();
            await foreach (var message in call.ResponseStream.ReadAllAsync()) messages.Add(message);
            Assert.DoesNotContain(messages, x => x.Type == Constants.MessageError);
        }

        await source.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var id = Assert.Single(data.Store.AssessmentsOnDisk());
        var before = (await data.Store.LatestRootAsync(id))!;
        using (var call = host.Client.Pause(new PauseRequest { Id = id.ToString() }))
            await foreach (var message in call.ResponseStream.ReadAllAsync())
                Assert.NotEqual(Constants.MessageError, message.Type);
        Assert.Equal(ScanStatus.Paused, await host.Coordinator!.WaitForCompletionAsync(id));
        source.BlockAt = null;
        using (var call = host.Client.Restart(new RestartRequest { Id = id.ToString(), Threads = 1 }))
            await foreach (var message in call.ResponseStream.ReadAllAsync())
                Assert.NotEqual(Constants.MessageError, message.Type);
        Assert.Equal(ScanStatus.Finished, await host.Coordinator.WaitForCompletionAsync(id));
        var after = (await data.Store.LatestRootAsync(id))!;
        Assert.Equal(before.RunId, after.RunId);
        Assert.Equal(before.SnapshotId, after.SnapshotId);
        Assert.Equal(PipelineRunSettings.Read(before).AnalysisRunId, PipelineRunSettings.Read(after).AnalysisRunId);
        var listed = await host.Client.ListAsync(new ListRequest());
        var row = Assert.Single(listed.Status, x => x.Id == id.ToString());
        Assert.Equal("Classic", row.Mode);
        Assert.Equal("Finished", row.Status);
        Assert.True((await host.Client.PingAsync(new Empty())).UpAndRunning);
        Assert.Equal(0, environment.OnlineRequests);
        Assert.Equal(0, host.Counter.Calls);
    }

    [Fact]
    public async Task Existing_stop_rpc_settles_new_collection_before_stopping_the_engine()
    {
        using var data = new StoreCase("classic-native-stop");
        var source = new ClassicPageFixture { BlockAt = "discovery", IncludePhysicalFacts = true };
        await using var host = await NativePipelineHost.StartAsync(data.Store, ClassicPageModule.Registry(source.OpenAsync), new());
        using (var call = host.Client.Start(ClassicPageFixture.MakeOptions()))
            await foreach (var message in call.ResponseStream.ReadAllAsync())
                Assert.NotEqual(Constants.MessageError, message.Type);
        await source.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var id = Assert.Single(data.Store.AssessmentsOnDisk());
        await host.Client.StopAsync(new StopRequest());
        Assert.Equal(ScanStatus.Terminated, await host.Coordinator!.WaitForCompletionAsync(id));
        using var db = data.Store.CreateContext(id, readOnly: true);
        Assert.False((await db.SourceSnapshots.SingleAsync()).IsSealed);
        Assert.Equal(0, host.Counter.Calls);
    }

    [Theory]
    [InlineData("collect", "--mode", "Classic")]
    [InlineData("analyze", "--id", "11111111-1111-1111-1111-111111111111")]
    [InlineData("start-pipeline", "--mode", "Classic")]
    [InlineData("start", "--mode", "ClassicPage")]
    public async Task No_parallel_scan_commands_or_modes_are_exposed(params string[] arguments)
    {
        using var data = new StoreCase("classic-public-surface");
        await using var host = await NativePipelineHost.StartAsync(data.Store, new(), new());
        Assert.NotEmpty(host.Parser.Parse(arguments).Errors);
        Assert.NotEmpty(host.Parser.Parse(["start", "--module", "classicpage"]).Errors);
        Assert.DoesNotContain(PnPScanner.Descriptor.Methods, method => method.Name is "Collect" or "Analyze" or "StartPipeline");
        Assert.DoesNotContain(System.Enum.GetNames<Mode>(), x => x == "ClassicPage");
    }

    [Fact]
    public async Task Unrelated_native_modes_keep_the_existing_start_path()
    {
        using var data = new StoreCase("classic-unrelated-entry");
        var environment = new ForbiddenOnlineEnvironment();
        await using var host = await NativePipelineHost.StartAsync(data.Store, ClassicPageModule.Registry(), environment);
        using var call = host.Client.Start(new StartRequest
        {
            Mode = Mode.InfoPath.ToString(), Tenant = "offline.invalid", AuthMode = "InvalidFixtureAuth",
            Environment = "Production", ApplicationId = ClassicPageFixture.App, Threads = 1,
        });
        var messages = new List<StartStatus>();
        await foreach (var message in call.ResponseStream.ReadAllAsync()) messages.Add(message);
        Assert.Contains(messages, x => x.Type == Constants.MessageError && x.Status.Contains("InvalidFixtureAuth"));
        Assert.Empty(data.Store.AssessmentsOnDisk());
        Assert.Equal(0, environment.OnlineRequests);
    }
}
