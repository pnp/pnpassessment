#nullable enable
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PnP.Scanning.Core.Pipeline.Collection.Module;
using PnP.Scanning.Core.Pipeline.Contracts.List;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Pipeline.Analysis.Page;
using PnP.Scanning.Core.Pipeline.Orchestration;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Storage.Pipeline;
using Xunit;

namespace PnP.Scanning.Core.Tests.Pipeline;

[Trait("Category", "Pipeline")]
public sealed class ClassicReplacementRecoveryTests
{
    [Fact]
    public async Task Report_materialization_failure_rolls_back_and_restart_reuses_sealed_analysis_without_recollection()
    {
        using var data = new StoreCase("classic-report-rollback");
        var interceptor = new FailCompatibilityCommit();
        var store = new PipelineStore(data.DirectoryPath, id => new ScanContext(new DbContextOptionsBuilder<ScanContext>()
            .UseSqlite($"Data Source={data.Store.DatabasePath(id)};Pooling=False").AddInterceptors(interceptor).Options));
        var source = new ClassicPageFixture { IncludePhysicalFacts = true, IncludeBlog = true };
        var environment = new ForbiddenOnlineEnvironment();
        var coordinator = new PipelineCoordinator(store, ClassicPageModule.Registry(source.OpenAsync), environment);
        var ticket = await coordinator.StartPipelineAsync(new() { Collection = ClassicPageFixture.Request() });
        var assessment = Guid.Parse(ticket.AssessmentId);
        Assert.Equal(ScanStatus.Terminated, await coordinator.WaitForCompletionAsync(assessment));
        using (var db = store.CreateContext(assessment, readOnly: true))
        {
            Assert.Empty(await db.ClassicPages.ToListAsync());
            Assert.NotNull((await db.AnalysisRuns.SingleAsync()).ReportDigest);
            Assert.True((await db.SourceSnapshots.SingleAsync()).IsSealed);
            Assert.Equal(ScanStatus.Finished, (await db.PhaseRuns.SingleAsync(x => x.Kind == PhaseKind.Analysis)).Status);
        }
        var calls = source.Calls.OrderBy(x => x.Key).ToArray();
        environment.ForbidRestore = true;
        await coordinator.RestartAsync(assessment);
        Assert.Equal(ScanStatus.Finished, await coordinator.WaitForCompletionAsync(assessment));
        Assert.Equal(calls, source.Calls.OrderBy(x => x.Key));
        using var completed = store.CreateContext(assessment, readOnly: true);
        Assert.True(await completed.ClassicPages.AnyAsync(x => x.PageType == "BlogPage"));
        Assert.Single(await completed.AnalysisRuns.ToListAsync());
        Assert.True(await completed.History.AnyAsync(x => x.Message.Contains("Synthetic compatibility commit failure")));
    }

    private sealed class FailCompatibilityCommit : DbTransactionInterceptor
    {
        private bool failed;
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!failed && eventData.Context is ScanContext db && db.ClassicPages.Local.Any())
            {
                failed = true;
                throw new IOException("Synthetic compatibility commit failure");
            }
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public void Custom_display_form_items_keep_the_existing_supported_aspx_behavior_in_the_new_collector()
    {
        var source = new ClassicPageFixture();
        var batch = new ClassicPageBlogBatchSource(ClassicPageFixture.Site, "/", ClassicPageFixture.ListId,
            "Custom content", "/sites/a/Lists/Custom", [], null, SourceReadState.Complete,
            "/sites/a/Lists/Custom/DispForm.aspx", "ASPXPage",
            ContentTypeForms: [new ClassicContentTypeForm("0x0102", "/sites/a/Lists/Custom/Custom.aspx")]);
        var fields = new Dictionary<string, SourceField>
        {
            ["ID"] = SourceField.Of("Int32", 9),
            ["ContentTypeId"] = SourceField.Of("String", "0x010201"),
        };
        var page = ClassicBlogPageProjection.Analyze(Guid.NewGuid(), batch, fields, source.Web(), source.Options);
        Assert.Equal("/sites/a/Lists/Custom/Custom.aspx?ID=9", page!.PageUrl);
        Assert.Equal("ASPXPage", page.PageType);
        Assert.Equal("CP5", page.RemediationCode);
        Assert.Null(ClassicBlogPageProjection.Analyze(Guid.NewGuid(), batch with { ContentTypeForms = null },
            fields, source.Web(), source.Options));
    }
}
