using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.Scanners
{
    /// <summary>
    /// Reads which on-disk template a page was provisioned from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The server records this in <c>vti_setuppath</c> and it survives customization, which makes
    /// it the one client-visible signal that separates a system list form or view from user content
    /// without inferring anything from a URL. Discovery already collects it; this reads it.
    /// </para>
    /// <para>
    /// The observation keeps the whole <see cref="SetupPathPageFamily"/>, including the wiki and
    /// blog families that <c>PageScanComponent.FileLevelPageType</c> folds into the residual page
    /// type. Folding them there is correct for choosing an extractor — without the item there is no
    /// wiki field or post content to read, so claiming either type would overstate what was
    /// assessed — but the provisioning fact itself is proven, and a report that keeps only the
    /// extractor choice throws it away. Keeping both is the point: the template is one finding, and
    /// what could be read from the page is another.
    /// </para>
    /// </remarks>
    internal sealed class ProvisioningTemplateAnalyzer : IPageComponentAnalyzer
    {
        public PageComponent Component => PageComponent.ProvisioningTemplate;

        public Task<PageComponentObservation> AnalyzeAsync(PageAnalysisContext page,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Observe(page.Discovered));

        /// <summary>
        /// The synchronous read. Discovery already carries the setup path on the row, so there is no
        /// surface to go out to; the async signature belongs to the seam, not to this analyzer.
        /// </summary>
        internal static PageComponentObservation Observe(ClassicPageDiscovery discovered)
        {
            var setupPath = discovered?.SetupPath;
            var family = SetupPathClassifier.Classify(setupPath);

            // An unghosted file has no setup path because it was authored or uploaded, not
            // provisioned. That is a surface read and found empty, not a surface that failed.
            return family == SetupPathPageFamily.Unghosted
                ? PageComponentObservation.Absent(PageComponent.ProvisioningTemplate, setupPath, family)
                : PageComponentObservation.Present(PageComponent.ProvisioningTemplate, setupPath, family);
        }

        /// <summary>
        /// The family an observation carries. An observation from any other analyzer, or none at
        /// all, yields <see cref="SetupPathPageFamily.Unknown"/>: nothing is known about the
        /// template, which is the residual case rather than a claim that the file is unghosted.
        /// </summary>
        internal static SetupPathPageFamily FamilyOf(PageComponentObservation observation) =>
            observation != null && observation.Component == PageComponent.ProvisioningTemplate &&
            observation.TryValue<SetupPathPageFamily>(out var family)
                ? family
                : SetupPathPageFamily.Unknown;
    }
}
