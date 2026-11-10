using PnP.Scanning.Core.ClassicPages;
#nullable enable
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Pipeline.Orchestration;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Storage.Pipeline;
using Xunit;

namespace PnP.Scanning.Core.Tests.Pipeline;

/// <summary>
/// Serializes tests that share CLI console and loopback host state.
/// </summary>
[CollectionDefinition("Native pipeline CLI", DisableParallelization = true)]
public sealed class NativePipelineCollection;

/// <summary>
/// Verifies replacement behind existing CLI, lifecycle, and report entry points.
/// </summary>
[Collection("Native pipeline CLI")]
[Trait("Category", "Pipeline")]
public sealed class NativePipelineTests
{
    /// <summary>
    /// Verifies that existing report rpc exports real replacement results without online access or new csv contracts.
    /// </summary>
    [Fact]
    public async Task Existing_report_rpc_exports_real_replacement_results_without_online_access_or_new_csv_contracts()
    {
        using StoreCase? output = new StoreCase("classic-native-report");
        PipelineStore? store = new PipelineStore(AppContext.BaseDirectory);
        ClassicContentFixture? source = new ClassicContentFixture
        {
            IncludePhysicalFacts = true, IncludeBlog = true, IncludeRetiredFile = true
        };
        Guid assessment = Guid.Empty;
        try
        {
            await using NativePipelineHost? host = await NativePipelineHost.StartAsync(
                store,
                ClassicPageModule.Registry(source.OpenAsync),
                new());
            using (AsyncServerStreamingCall<StartStatus>? call = host.Client.Start(
                       ClassicContentFixture.MakeOptions(usage: true)))
                await foreach (StartStatus? message in call.ResponseStream.ReadAllAsync())
                {
                    Assert.NotEqual(Constants.MessageError, message.Type);
                    System.Text.RegularExpressions.Match? match = System.Text.RegularExpressions.Regex.Match(
                        message.Status,
                        @"Assessment id = ([0-9a-f-]{36})");
                    if (match.Success) assessment = Guid.Parse(match.Groups[1].Value);
                }

            Assert.NotEqual(Guid.Empty, assessment);
            Assert.Equal(ScanStatus.Finished, await host.Coordinator!.WaitForCompletionAsync(assessment));
            Dictionary<string, int>? calls = source.Calls.ToDictionary(x => x.Key, x => x.Value);
            for (int repeat = 0; repeat < 2; repeat++)
            {
                using AsyncServerStreamingCall<ReportStatus>? report = host.Client.Report(
                    new ReportRequest
                    {
                        Id = assessment.ToString(),
                        Mode = ReportMode.CsvOnly.ToString(),
                        Path = output.DirectoryPath,
                        Delimiter = ",",
                    });
                List<ReportStatus>? messages = new List<ReportStatus>();
                await foreach (ReportStatus? message in report.ResponseStream.ReadAllAsync()) messages.Add(message);
                Assert.DoesNotContain(messages, x => x.Type == Constants.MessageError);
                Assert.Contains(messages, x => !string.IsNullOrEmpty(x.ReportPath));
            }

            Assert.Contains(
                "Home.aspx",
                await File.ReadAllTextAsync(Path.Combine(output.DirectoryPath, "classicpages.csv")));
            Assert.Contains(
                "BlogPage",
                await File.ReadAllTextAsync(Path.Combine(output.DirectoryPath, "classicpages.csv")));
            Assert.Contains("Classic", await File.ReadAllTextAsync(Path.Combine(output.DirectoryPath, "scans.csv")));
            using ScanContext? db = store.CreateContext(assessment, readOnly: true);
            Assert.True(
                await db.ClassicPageAuditUsages.AnyAsync(x => x.AuditViewsCount == 1 && x.AuditEditsCount == 1));
            Assert.Equal(calls.OrderBy(x => x.Key), source.Calls.OrderBy(x => x.Key));
            Assert.Equal(0, host.Counter.Calls);
        }
        finally
        {
            if (assessment != Guid.Empty)
            {
                using ScanContext? context = new ScanContext(assessment);
                Microsoft.Data.Sqlite.SqliteConnection.ClearPool(
                    (Microsoft.Data.Sqlite.SqliteConnection)context.Database.GetDbConnection());
                Directory.Delete(Path.Combine(AppContext.BaseDirectory, assessment.ToString()), true);
            }
        }
    }

    /// <summary>
    /// Verifies that existing restart adopts a historical classic assessment without using old page execution.
    /// </summary>
    [Fact]
    public async Task Existing_restart_adopts_a_historical_classic_assessment_without_using_old_page_execution()
    {
        using StoreCase? data = new StoreCase("classic-historical-restart");
        Guid assessment = Guid.NewGuid();
        Directory.CreateDirectory(Path.GetDirectoryName(data.Store.DatabasePath(assessment))!);
        using (ScanContext? db = data.Store.CreateContext(assessment))
        {
            await db.Database.MigrateAsync();
            StartRequest? request = ClassicContentFixture.MakeOptions();
            db.Scans.Add(
                new PnP.Scanning.Core.Storage.Scan
                {
                    ScanId = assessment,
                    CLIMode = "Classic",
                    CLITenant = request.Tenant,
                    CLITenantId = request.TenantId,
                    CLIEnvironment = request.Environment,
                    CLIAuthMode = request.AuthMode,
                    CLIApplicationId = request.ApplicationId,
                    CLIThreads = 1,
                    Status = ScanStatus.Paused,
                    CLISiteList = request.SitesList,
                });
            db.Properties.AddRange(
                request.Properties.Select(x =>
                    new Property
                    {
                        ScanId = assessment, Name = x.Property, Type = x.Type, Value = x.Value,
                    }));
            await db.SaveChangesAsync();
        }

        ClassicContentFixture? source = new ClassicContentFixture { IncludePhysicalFacts = true };
        await using NativePipelineHost? host = await NativePipelineHost.StartAsync(
            data.Store,
            ClassicPageModule.Registry(source.OpenAsync),
            new());
        using (AsyncServerStreamingCall<RestartStatus>? restart =
               host.Client.Restart(new RestartRequest { Id = assessment.ToString() }))
            await foreach (RestartStatus? message in restart.ResponseStream.ReadAllAsync())
                Assert.NotEqual(Constants.MessageError, message.Type);
        Assert.Equal(ScanStatus.Finished, await host.Coordinator!.WaitForCompletionAsync(assessment));
        using ScanContext? saved = data.Store.CreateContext(assessment);
        Assert.Equal(assessment, (await saved.Scans.SingleAsync()).ScanId);
        Assert.Single(await saved.SourceSnapshots.ToListAsync());
        Assert.True(await saved.ClassicPages.AnyAsync());
        Assert.Equal(0, host.Counter.Calls);
    }

    /// <summary>
    /// Verifies that existing start command routes pages and mixed components through the new pipeline.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Existing_start_command_routes_pages_and_mixed_components_through_the_new_pipeline(bool mixed)
    {
        using StoreCase? data = new StoreCase("classic-existing-cli");
        ClassicContentFixture? source = new ClassicContentFixture
        {
            IncludePhysicalFacts = true, IncludeBlog = true, IncludeRetiredFile = true
        };
        await using NativePipelineHost? host = await NativePipelineHost.StartAsync(
            data.Store,
            ClassicPageModule.Registry(source.OpenAsync),
            new());
        List<string>? arguments = new List<string>
        {
            "start",
            "--mode",
            "Classic",
            "--classicinclude",
            "Pages",
            "--tenant",
            "contoso.sharepoint.com",
            "--tenantid",
            ClassicContentFixture.SiteCollectionId.ToString(),
            "--applicationid",
            ClassicContentFixture.App,
            "--authmode",
            "application",
            "--certpath",
            "My|CurrentUser|SYNTHETIC",
            "--skipusageinformation",
            "--threads",
            "1",
        };
        if (mixed) arguments.InsertRange(5, ["Lists", "InfoPath", "Extensibility"]);
        (int ExitCode, PhaseReply? Ticket, string Output, string Error) result =
            await host.InvokeAsync(arguments.ToArray());
        Assert.Equal(0, result.ExitCode);
        await StoreCase.WaitUntilAsync(() => Task.FromResult(data.Store.AssessmentsOnDisk().Any()));
        Guid assessment = Assert.Single(data.Store.AssessmentsOnDisk());
        Assert.Equal(ScanStatus.Finished, await host.Coordinator!.WaitForCompletionAsync(assessment));
        Assert.Equal(0, host.Counter.Calls);
        Assert.True(source.Calls.ContainsKey("blog-lists"));
        Assert.Equal(mixed, source.Calls.ContainsKey("components"));
        using ScanContext? db = data.Store.CreateContext(assessment);
        Assert.Equal("Classic", (await db.Scans.SingleAsync()).CLIMode);
        Assert.True(await db.ClassicPages.AnyAsync(x => x.PageType == "BlogPage"));
        Assert.False(await db.ClassicPages.AnyAsync(x => x.PageType == "DelveBlogPage"));
        Assert.True(await db.SPFiles.AnyAsync(x => x.FileName.EndsWith(".pointpub")));
        Assert.Equal(mixed ? 1 : 0, await db.ClassicLists.CountAsync());
        Assert.Single(await db.SourceSnapshots.ToListAsync());
        Assert.Single(await db.AnalysisRuns.ToListAsync());
    }

    /// <summary>
    /// Verifies that default all classic selection uses the same replacement.
    /// </summary>
    [Fact]
    public async Task Default_all_classic_selection_uses_the_same_replacement()
    {
        using StoreCase? data = new StoreCase("classic-default-all");
        ClassicContentFixture? source = new ClassicContentFixture { IncludePhysicalFacts = true };
        await using NativePipelineHost? host = await NativePipelineHost.StartAsync(
            data.Store,
            ClassicPageModule.Registry(source.OpenAsync),
            new());
        (int ExitCode, PhaseReply? Ticket, string Output, string Error) result = await host.InvokeAsync(
            "start",
            "--mode",
            "Classic",
            "--tenant",
            "contoso.sharepoint.com",
            "--tenantid",
            ClassicContentFixture.SiteCollectionId.ToString(),
            "--applicationid",
            ClassicContentFixture.App,
            "--authmode",
            "application",
            "--certpath",
            "My|CurrentUser|SYNTHETIC",
            "--skipusageinformation");
        Assert.Equal(0, result.ExitCode);
        await StoreCase.WaitUntilAsync(() => Task.FromResult(data.Store.AssessmentsOnDisk().Any()));
        Guid id = Assert.Single(data.Store.AssessmentsOnDisk());
        Assert.Equal(ScanStatus.Finished, await host.Coordinator!.WaitForCompletionAsync(id));
        Assert.Equal(1, source.Calls["components"]);
        Assert.Equal(0, host.Counter.Calls);
    }

    /// <summary>
    /// Verifies that existing native Start pause restart and list preserve phase identity.
    /// </summary>
    [Fact]
    public async Task Existing_native_Start_pause_restart_and_list_preserve_phase_identity()
    {
        using StoreCase? data = new StoreCase("classic-native-restart");
        ClassicContentFixture? source =
            new ClassicContentFixture { BlockAt = "discovery", IncludePhysicalFacts = true };
        ForbiddenOnlineEnvironment? environment = new ForbiddenOnlineEnvironment();
        await using NativePipelineHost? host = await NativePipelineHost.StartAsync(
            data.Store,
            ClassicPageModule.Registry(source.OpenAsync),
            environment);
        using (AsyncServerStreamingCall<StartStatus>? call = host.Client.Start(ClassicContentFixture.MakeOptions()))
        {
            List<StartStatus>? messages = new List<StartStatus>();
            await foreach (StartStatus? message in call.ResponseStream.ReadAllAsync()) messages.Add(message);
            Assert.DoesNotContain(messages, x => x.Type == Constants.MessageError);
        }

        await source.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Guid id = Assert.Single(data.Store.AssessmentsOnDisk());
        PhaseRunRow? before = (await data.Store.LatestRootAsync(id))!;
        using (AsyncServerStreamingCall<PauseStatus>? call = host.Client.Pause(new PauseRequest { Id = id.ToString() }))
            await foreach (PauseStatus? message in call.ResponseStream.ReadAllAsync())
                Assert.NotEqual(Constants.MessageError, message.Type);
        Assert.Equal(ScanStatus.Paused, await host.Coordinator!.WaitForCompletionAsync(id));
        source.BlockAt = null;
        using (AsyncServerStreamingCall<RestartStatus>? call =
               host.Client.Restart(new RestartRequest { Id = id.ToString(), Threads = 1 }))
            await foreach (RestartStatus? message in call.ResponseStream.ReadAllAsync())
                Assert.NotEqual(Constants.MessageError, message.Type);
        Assert.Equal(ScanStatus.Finished, await host.Coordinator.WaitForCompletionAsync(id));
        PhaseRunRow? after = (await data.Store.LatestRootAsync(id))!;
        Assert.Equal(before.RunId, after.RunId);
        Assert.Equal(before.SnapshotId, after.SnapshotId);
        Assert.Equal(PipelineRunSettings.Read(before).AnalysisRunId, PipelineRunSettings.Read(after).AnalysisRunId);
        ListReply? listed = await host.Client.ListAsync(new ListRequest());
        ListScanResponse? row = Assert.Single(listed.Status, x => x.Id == id.ToString());
        Assert.Equal("Classic", row.Mode);
        Assert.Equal("Finished", row.Status);
        Assert.True((await host.Client.PingAsync(new Empty())).UpAndRunning);
        Assert.Equal(0, environment.OnlineRequests);
        Assert.Equal(0, host.Counter.Calls);
    }

    /// <summary>
    /// Verifies that existing stop rpc settles new collection before stopping the engine.
    /// </summary>
    [Fact]
    public async Task Existing_stop_rpc_settles_new_collection_before_stopping_the_engine()
    {
        using StoreCase? data = new StoreCase("classic-native-stop");
        ClassicContentFixture? source =
            new ClassicContentFixture { BlockAt = "discovery", IncludePhysicalFacts = true };
        await using NativePipelineHost? host = await NativePipelineHost.StartAsync(
            data.Store,
            ClassicPageModule.Registry(source.OpenAsync),
            new());
        using (AsyncServerStreamingCall<StartStatus>? call = host.Client.Start(ClassicContentFixture.MakeOptions()))
            await foreach (StartStatus? message in call.ResponseStream.ReadAllAsync())
                Assert.NotEqual(Constants.MessageError, message.Type);
        await source.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Guid id = Assert.Single(data.Store.AssessmentsOnDisk());
        await host.Client.StopAsync(new StopRequest());
        Assert.Equal(ScanStatus.Terminated, await host.Coordinator!.WaitForCompletionAsync(id));
        using ScanContext? db = data.Store.CreateContext(id, readOnly: true);
        Assert.False((await db.SourceSnapshots.SingleAsync()).IsSealed);
        Assert.Equal(0, host.Counter.Calls);
    }

    /// <summary>
    /// Verifies that no parallel scan commands or modes are exposed.
    /// </summary>
    [Theory]
    [InlineData("collect", "--mode", "Classic")]
    [InlineData("analyze", "--id", "11111111-1111-1111-1111-111111111111")]
    [InlineData("start-pipeline", "--mode", "Classic")]
    [InlineData("start", "--mode", "ClassicPage")]
    public async Task No_parallel_scan_commands_or_modes_are_exposed(params string[] arguments)
    {
        using StoreCase? data = new StoreCase("classic-public-surface");
        await using NativePipelineHost? host = await NativePipelineHost.StartAsync(data.Store, new(), new());
        Assert.NotEmpty(host.Parser.Parse(arguments).Errors);
        Assert.NotEmpty(host.Parser.Parse(["start", "--module", "classicpage"]).Errors);
        Assert.DoesNotContain(
            PnPScanner.Descriptor.Methods,
            method => method.Name is "Collect" or "Analyze" or "StartPipeline");
        Assert.DoesNotContain(System.Enum.GetNames<Mode>(), x => x == "ClassicPage");
    }

    /// <summary>
    /// Verifies that unrelated native modes keep the existing start path.
    /// </summary>
    [Fact]
    public async Task Unrelated_native_modes_keep_the_existing_start_path()
    {
        using StoreCase? data = new StoreCase("classic-unrelated-entry");
        ForbiddenOnlineEnvironment? environment = new ForbiddenOnlineEnvironment();
        await using NativePipelineHost? host = await NativePipelineHost.StartAsync(
            data.Store,
            ClassicPageModule.Registry(),
            environment);
        using AsyncServerStreamingCall<StartStatus>? call = host.Client.Start(
            new StartRequest
            {
                Mode = Mode.InfoPath.ToString(),
                Tenant = "offline.invalid",
                AuthMode = "InvalidFixtureAuth",
                Environment = "Production",
                ApplicationId = ClassicContentFixture.App,
                Threads = 1,
            });
        List<StartStatus>? messages = new List<StartStatus>();
        await foreach (StartStatus? message in call.ResponseStream.ReadAllAsync()) messages.Add(message);
        Assert.Contains(messages, x => x.Type == Constants.MessageError && x.Status.Contains("InvalidFixtureAuth"));
        Assert.Empty(data.Store.AssessmentsOnDisk());
        Assert.Equal(0, environment.OnlineRequests);
    }
}