namespace PnP.Scanning.Core.Scanners
{
    /// <summary>
    /// Reads one component of a page and reports what it saw.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An analyzer answers one question — "does this page have a wiki field", "which template was
    /// it provisioned from", "what is in its web part zones" — and never the question "what kind of
    /// page is this". No analyzer may contradict another: a page that is provisioned from a wiki
    /// template and also has web part zones has both, and both are reported.
    /// </para>
    /// <para>
    /// An analyzer that cannot read its surface says so. Returning
    /// <see cref="PageComponentOutcome.Absent"/> for a surface it never reached would assert
    /// evidence it does not have.
    /// </para>
    /// <para>
    /// This mirrors <c>IAspxDiscoveryProvider</c> on the discovery side: a narrow seam with one
    /// responsibility, so the orchestrator above it needs to know nothing about SharePoint.
    /// </para>
    /// </remarks>
    internal interface IPageComponentAnalyzer
    {
        /// <summary>The component this analyzer reports on, for diagnostics and ordering.</summary>
        PageComponent Component { get; }

        /// <summary>
        /// Reads the surface and returns what was established, or <c>null</c> to report nothing at
        /// all — which is distinct from reporting that nothing is there.
        /// </summary>
        Task<PageComponentObservation> AnalyzeAsync(PageAnalysisContext page,
            CancellationToken cancellationToken = default);
    }
}
