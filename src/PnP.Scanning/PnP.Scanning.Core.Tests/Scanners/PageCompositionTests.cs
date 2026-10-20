using FluentAssertions;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Storage;
using Xunit;

namespace PnP.Scanning.Core.Tests.Scanners
{
    /// <summary>
    /// The analysis seam. A page is a composition, so analysis accumulates independent observations
    /// rather than narrowing to one label; these cover the two properties that makes true — nothing
    /// an analyzer does removes a page or a previous finding, and two analyzers that disagree about
    /// the same component both survive.
    ///
    /// The funnel's whole purpose is that the stages below discovery subtract nothing. A classifier
    /// that drops what it cannot explain produces a count that silently understates the tenant, which
    /// is the failure these tests exist to prevent at the seam rather than discover in a scan.
    /// </summary>
    public class PageCompositionTests
    {
        [Fact]
        public async Task Every_analyzer_contributes_even_when_they_disagree()
        {
            var report = await Run(
                Stub(PageComponent.ProvisioningTemplate, PageComponentOutcome.Present, "wkpstd.aspx"),
                Stub(PageComponent.ProvisioningTemplate, PageComponentOutcome.Absent, "no template"));

            report.For(PageComponent.ProvisioningTemplate).Should().HaveCount(2,
                "a disagreement between two readings is itself a finding, so neither may overwrite the other");
            report.IsPresent(PageComponent.ProvisioningTemplate).Should().BeTrue();
        }

        [Fact]
        public void Asking_for_one_answer_about_a_disputed_component_is_refused()
        {
            var report = new PageCompositionReport();
            report.Add(PageComponentObservation.Present(PageComponent.ProvisioningTemplate, "a"));
            report.Add(PageComponentObservation.Absent(PageComponent.ProvisioningTemplate, "b"));

            report.Invoking(value => value.Single(PageComponent.ProvisioningTemplate))
                .Should().Throw<InvalidOperationException>(
                    "silently picking one of two readings is the overwrite this layer exists to remove");
        }

        [Fact]
        public async Task A_failing_analyzer_becomes_a_gap_and_does_not_stop_the_rest()
        {
            var report = await Run(
                Throwing(PageComponent.ProvisioningTemplate, new InvalidOperationException("denied")),
                Stub(PageComponent.ProvisioningTemplate, PageComponentOutcome.Present, "wkpstd.aspx"));

            report.Should().HaveCount(2, "one surface failing says nothing about another");

            var failure = report.For(PageComponent.ProvisioningTemplate)[0];
            failure.Outcome.Should().Be(PageComponentOutcome.Unreadable)
                .And.NotBe(PageComponentOutcome.Absent, "an unread surface is not a confirmed empty one");
            failure.Detail.Should().Contain("denied", "a gap with no reason cannot be acted on");
        }

        [Fact]
        public async Task An_analyzer_that_reports_nothing_leaves_no_trace()
        {
            var report = await Run(Silent(PageComponent.ProvisioningTemplate));

            report.Should().BeEmpty();
            report.Single(PageComponent.ProvisioningTemplate).Should().BeNull(
                "an unobserved component is distinct from one looked for and not found");
        }

        [Fact]
        public async Task Cancellation_is_not_recorded_as_a_property_of_the_page()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var page = Page(Row(setupPath: @"wkpstd.aspx"));
            var analyzer = new PageCompositionAnalyzer(new[] { new ProvisioningTemplateAnalyzer() });

            await analyzer.Invoking(value => value.AnalyzeAsync(page, cancellation.Token))
                .Should().ThrowAsync<OperationCanceledException>();

            page.Report.Should().BeEmpty("the scan ending is not an observation about this page");
        }

        [Fact]
        public async Task A_ghosted_file_reports_the_template_it_was_provisioned_from()
        {
            await AssertFamily(@"1033\STS\doctemp\smartpgs\wkpstd.aspx", SetupPathPageFamily.WikiPage);
            await AssertFamily(@"1033\STS\doctemp\smartpgs\spstd1.aspx", SetupPathPageFamily.WebPartPage);
            await AssertFamily(@"1033\STS\lists\pages\form.aspx", SetupPathPageFamily.ListForm);

            static async Task AssertFamily(string setupPath, SetupPathPageFamily expected)
            {
                var report = await Run(Page(Row(setupPath)), new ProvisioningTemplateAnalyzer());

                var observation = report.Single(PageComponent.ProvisioningTemplate);
                observation.Outcome.Should().Be(PageComponentOutcome.Present);
                observation.Evidence.Should().Be(setupPath,
                    "the reading must stay re-derivable from what was read");
                ProvisioningTemplateAnalyzer.FamilyOf(observation).Should().Be(expected);
            }
        }

        [Fact]
        public async Task An_unghosted_file_reports_a_confirmed_absence_not_a_gap()
        {
            var report = await Run(Page(Row(setupPath: null)), new ProvisioningTemplateAnalyzer());

            report.Single(PageComponent.ProvisioningTemplate).Outcome
                .Should().Be(PageComponentOutcome.Absent)
                .And.NotBe(PageComponentOutcome.Unreadable,
                    "the row carried no setup path because the file was authored, not because a read failed");
        }

        /// <summary>
        /// The wiki and blog families are the ones <c>FileLevelPageType</c> folds into the residual
        /// ASPXPage, because without a list item there is no wiki field or post content to read and
        /// claiming either type would overstate what was assessed. The provisioning fact is proven
        /// regardless, and the observation keeps it — which is the difference between a single label
        /// and a report.
        /// </summary>
        [Fact]
        public async Task A_family_the_page_type_discards_survives_in_the_observation()
        {
            await AssertSurvives(@"1033\STS\doctemp\smartpgs\wkpstd.aspx", SetupPathPageFamily.WikiPage);
            await AssertSurvives(@"Features\BlogContent\Post.aspx", SetupPathPageFamily.BlogPost);

            static async Task AssertSurvives(string setupPath, SetupPathPageFamily expected)
            {
                var report = await Run(Page(Row(setupPath)), new ProvisioningTemplateAnalyzer());

                PageScanComponent.FileLevelPageType(SetupPathClassifier.Classify(setupPath))
                    .Should().Be("ASPXPage",
                        "the extractor choice cannot claim a type it has no surface to read");
                ProvisioningTemplateAnalyzer.FamilyOf(report.Single(PageComponent.ProvisioningTemplate))
                    .Should().Be(expected,
                        "the provisioning fact is proven and must not be lost with the label");
            }
        }

        /// <summary>
        /// Reading a typed value off an observation is only valid when the observation is the one
        /// that produced it. A report hands back observations by component, so a caller indexing one
        /// out can reach the wrong analyzer's; answering Unknown there keeps that mistake from
        /// becoming a confident, wrong family.
        /// </summary>
        [Fact]
        public void A_family_is_not_read_off_another_analyzers_observation()
        {
            ProvisioningTemplateAnalyzer.FamilyOf(null).Should().Be(SetupPathPageFamily.Unknown)
                .And.NotBe(SetupPathPageFamily.Unghosted, "nothing was read, which is not a read that found nothing");

            var foreign = new PageComponentObservation((PageComponent)int.MaxValue,
                PageComponentOutcome.Present, "wkpstd.aspx", Value: SetupPathPageFamily.WikiPage);
            ProvisioningTemplateAnalyzer.FamilyOf(foreign).Should().Be(SetupPathPageFamily.Unknown);
        }

        private static async Task<PageCompositionReport> Run(params IPageComponentAnalyzer[] analyzers) =>
            await Run(Page(Row(@"wkpstd.aspx")), analyzers);

        private static Task<PageCompositionReport> Run(PageAnalysisContext page,
            params IPageComponentAnalyzer[] analyzers) =>
            new PageCompositionAnalyzer(analyzers).AnalyzeAsync(page);

        // None of these analyzers reads a per-web surface, so an empty web context is the honest
        // stand-in: substituting a populated one would imply reads that never happen.
        private static PageAnalysisContext Page(ClassicPageDiscovery row) =>
            new(new ClassicPageWebContext(), row);

        private static ClassicPageDiscovery Row(string setupPath) =>
            new() { Url = "/sites/team/SitePages/home.aspx", SetupPath = setupPath };

        private static IPageComponentAnalyzer Stub(PageComponent component,
            PageComponentOutcome outcome, string evidence) =>
            new DelegateAnalyzer(component, _ => new PageComponentObservation(component, outcome, evidence));

        private static IPageComponentAnalyzer Silent(PageComponent component) =>
            new DelegateAnalyzer(component, _ => null);

        private static IPageComponentAnalyzer Throwing(PageComponent component, Exception error) =>
            new DelegateAnalyzer(component, _ => throw error);

        private sealed class DelegateAnalyzer : IPageComponentAnalyzer
        {
            private readonly Func<PageAnalysisContext, PageComponentObservation> analyze;

            internal DelegateAnalyzer(PageComponent component,
                Func<PageAnalysisContext, PageComponentObservation> analyze)
            {
                Component = component;
                this.analyze = analyze;
            }

            public PageComponent Component { get; }

            public Task<PageComponentObservation> AnalyzeAsync(PageAnalysisContext page,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(analyze(page));
        }
    }
}
