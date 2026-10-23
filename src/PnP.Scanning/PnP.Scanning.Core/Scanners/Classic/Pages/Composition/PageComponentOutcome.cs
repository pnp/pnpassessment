namespace PnP.Scanning.Core.Scanners
{
    /// <summary>
    /// What an analyzer established about one component of one page.
    /// </summary>
    /// <remarks>
    /// The distinction between <see cref="Absent"/>, <see cref="NotApplicable"/> and
    /// <see cref="Unreadable"/> is the point of the type, and the same distinction the discovery
    /// side already makes: a surface that was read and found empty is evidence, a surface that does
    /// not exist on this page carries no evidence either way, and a surface that exists but could
    /// not be read is a gap. Collapsing them is how an unread page comes to be reported as a page
    /// with nothing on it.
    /// </remarks>
    internal enum PageComponentOutcome
    {
        /// <summary>The component is on this page. <c>Evidence</c> says how it was recognised.</summary>
        Present,

        /// <summary>The surface was read, and the component is not there.</summary>
        Absent,

        /// <summary>
        /// This page has no such surface at all, so its absence says nothing about the page. A file
        /// with no backing list item has no item fields to be absent from.
        /// </summary>
        NotApplicable,

        /// <summary>
        /// The surface exists but could not be read: denied, failed, or never reached. Nothing may
        /// be concluded from it, in either direction.
        /// </summary>
        Unreadable,
    }
}
