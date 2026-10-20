using Microsoft.SharePoint.Client;
using PnP.Core.Model.SharePoint;
using PnP.Core.QueryModel;
using PnP.Core.Services;
using System.Linq.Expressions;

namespace PnP.Scanning.Core.Scanners
{
    /// <summary>
    /// Everything the Classic page assessment needs from the web it is running against, resolved
    /// once per web.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The per-web resources are expensive and shared: one <see cref="PnPContext"/>, one CSOM
    /// <see cref="ClientContext"/>, one loaded list set, and the reads derived from them. They are
    /// created by <c>ClassicScanner</c> and handed down, so nothing below may open its own. Passing
    /// them as one value rather than as loose parameters is what lets an analysis step declare that
    /// dependency in its signature instead of acquiring it.
    /// </para>
    /// <para>
    /// This is the analysis-side counterpart of what
    /// <c>ISharePointAspxRestClientFactory</c> does for discovery: it owns the per-web lifetime so
    /// the steps above it do not have to. It does not own disposal — the contexts outlive one page
    /// assessment and belong to the scanner.
    /// </para>
    /// </remarks>
    internal sealed class ClassicPageWebContext
    {
        // Internal rather than private so a test can construct the empty context an analyzer that
        // reads no per-web surface never touches. CreateAsync remains the only way to get a
        // populated one, which is the invariant that matters.
        internal ClassicPageWebContext() { }

        internal ScannerBase Scanner { get; private init; }

        internal PnPContext Pnp { get; private init; }

        internal ClientContext Csom { get; private init; }

        internal ClassicOptions Options { get; private init; }

        /// <summary>The web's lists as already loaded by the scanner, unfiltered.</summary>
        internal IReadOnlyList<IList> Lists { get; private init; }

        /// <summary>
        /// Root folder URLs of <see cref="Lists"/>. A file inside one of these is list-backed even
        /// when its discovery record carries no list identity, which separates an unobserved
        /// association from an absent one.
        /// </summary>
        internal IReadOnlyList<string> ListRootFolderUrls { get; private init; }

        /// <summary>The web's welcome page, which drives the HomePage flag and the HomePageOnly filter.</summary>
        internal string WelcomePage { get; private init; }

        /// <summary>
        /// False when the welcome page could not be read, so an absent value means unknown rather
        /// than "this web has none".
        /// </summary>
        internal bool WelcomePageKnown { get; private init; }

        internal bool SitePublishingEnabled { get; private init; }

        internal bool WebPublishingEnabled { get; private init; }

        internal Guid ScanId => Scanner.ScanId;

        internal CancellationToken CancellationToken =>
            Scanner.ScanManager.GetCancellationTokenSource(Scanner.ScanId).Token;

        internal bool IsCancellationRequested => CancellationToken.IsCancellationRequested;

        /// <summary>
        /// Resolves the per-web reads once. The welcome page read is the only network call made
        /// here; the features and lists were requested when the contexts were created.
        /// </summary>
        internal static async Task<ClassicPageWebContext> CreateAsync(ScannerBase scanner,
            PnPContext pnp, ClientContext csom)
        {
            ArgumentNullException.ThrowIfNull(scanner);
            ArgumentNullException.ThrowIfNull(pnp);
            ArgumentNullException.ThrowIfNull(csom);

            var (welcomePage, welcomePageKnown) = await ReadWelcomePageAsync(csom).ConfigureAwait(false);
            var lists = pnp.Web.Lists.AsRequested().ToList();

            return new ClassicPageWebContext
            {
                Scanner = scanner,
                Pnp = pnp,
                Csom = csom,
                Options = ((ClassicScanner)scanner).Options,
                Lists = lists,
                ListRootFolderUrls = lists.Select(value => value.RootFolder?.ServerRelativeUrl)
                    .Where(value => !string.IsNullOrWhiteSpace(value)).ToArray(),
                WelcomePage = welcomePage,
                WelcomePageKnown = welcomePageKnown,
                SitePublishingEnabled = FeatureEnabled(pnp.Site.Features, PageScanComponent.FeatureId_Site_Publishing),
                WebPublishingEnabled = FeatureEnabled(pnp.Web.Features, PageScanComponent.FeatureId_Web_Publishing),
            };
        }

        internal IList FindList(Guid? listId) =>
            listId == null ? null : Lists.FirstOrDefault(value => value.Id == listId);

        internal bool WebFeatureEnabled(Guid feature) => FeatureEnabled(Pnp.Web.Features, feature);

        // Reads the web's welcome page (server-relative-from-web). An unavailable read stays distinct
        // from a successful empty value, because an empty WelcomePage legitimately means default.aspx
        // while a failed read remains unknown — and reporting unknown as absent would filter every
        // page out of a --homepageonly run.
        private static async Task<(string WelcomePage, bool IsKnown)> ReadWelcomePageAsync(ClientContext csom)
        {
            try
            {
                var rootFolder = csom.Web.RootFolder;
                csom.Load(rootFolder, folder => folder.WelcomePage);
                await csom.ExecuteQueryAsync().ConfigureAwait(false);

                return (rootFolder.WelcomePage ?? "", true);
            }
            catch
            {
                return (null, false);
            }
        }

        private static bool FeatureEnabled(IFeatureCollection features, Guid feature) =>
            features.AsRequested().FirstOrDefault(value => value.DefinitionId == feature) != null;
    }
}
