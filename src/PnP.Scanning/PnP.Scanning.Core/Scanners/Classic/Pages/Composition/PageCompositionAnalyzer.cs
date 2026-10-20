namespace PnP.Scanning.Core.Scanners
{
    /// <summary>
    /// Runs every analyzer over one page and accumulates what they found.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The accumulation never narrows. A page is not dropped, skipped or downgraded by anything
    /// that happens here: an analyzer that throws contributes an
    /// <see cref="PageComponentOutcome.Unreadable"/> observation carrying the failure, and the
    /// remaining analyzers still run. That is the difference between a page that was read and found
    /// to have nothing, and a page nobody managed to read — a distinction the previous
    /// control-flow-based design could not express, because a failure there removed the page.
    /// </para>
    /// <para>
    /// One analyzer's failure must not deny the next one its turn, because they read independent
    /// surfaces: a denied list item says nothing about whether the file's web part zones are
    /// readable.
    /// </para>
    /// </remarks>
    internal sealed class PageCompositionAnalyzer
    {
        private readonly IReadOnlyList<IPageComponentAnalyzer> analyzers;

        internal PageCompositionAnalyzer(IEnumerable<IPageComponentAnalyzer> analyzers)
        {
            ArgumentNullException.ThrowIfNull(analyzers);
            this.analyzers = analyzers.ToArray();
        }

        internal async Task<PageCompositionReport> AnalyzeAsync(PageAnalysisContext page,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(page);

            foreach (var analyzer in analyzers)
            {
                cancellationToken.ThrowIfCancellationRequested();

                PageComponentObservation observation;
                try
                {
                    observation = await analyzer.AnalyzeAsync(page, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Cancellation is the scan ending, not a property of the page. Reporting it as
                    // an unreadable component would persist a gap that says nothing about the page.
                    throw;
                }
                catch (Exception ex)
                {
                    observation = PageComponentObservation.Unreadable(analyzer.Component,
                        $"{ex.GetType().Name}: {ex.Message}");
                }

                if (observation != null)
                {
                    page.Report.Add(observation);
                }
            }

            return page.Report;
        }
    }
}
