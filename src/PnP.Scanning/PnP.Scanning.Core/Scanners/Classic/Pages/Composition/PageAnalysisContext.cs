using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.Scanners
{
    /// <summary>
    /// Everything known about one page at the moment an analyzer runs.
    /// </summary>
    /// <remarks>
    /// Each analyzer reads the surface it understands and adds to <see cref="Report"/>; the next one
    /// sees what the previous ones found. The context is therefore the funnel's carrier, and it only
    /// ever grows.
    /// </remarks>
    internal sealed class PageAnalysisContext
    {
        internal PageAnalysisContext(ClassicPageWebContext web, ClassicPageDiscovery discovered,
            IReadOnlyDictionary<string, object> itemFields = null)
        {
            Web = web ?? throw new ArgumentNullException(nameof(web));
            Discovered = discovered ?? throw new ArgumentNullException(nameof(discovered));
            ItemFields = itemFields;
        }

        /// <summary>The per-web capabilities. An analyzer takes what it needs from here, never its own.</summary>
        internal ClassicPageWebContext Web { get; }

        /// <summary>
        /// The discovery row: the page's established identity and whatever discovery already read
        /// about it, such as its setup path and content type.
        /// </summary>
        internal ClassicPageDiscovery Discovered { get; }

        /// <summary>
        /// The backing list item's field values, or <c>null</c> when no item was read — either
        /// because the page has none, or because the read was not possible. An analyzer that needs
        /// them reports <see cref="PageComponentOutcome.NotApplicable"/> or
        /// <see cref="PageComponentOutcome.Unreadable"/> accordingly rather than treating null as
        /// an empty page.
        /// </summary>
        internal IReadOnlyDictionary<string, object> ItemFields { get; }

        internal PageCompositionReport Report { get; } = new();
    }
}
