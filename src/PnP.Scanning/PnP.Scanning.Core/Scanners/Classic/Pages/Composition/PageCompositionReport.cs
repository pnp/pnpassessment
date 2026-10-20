using System.Collections;

namespace PnP.Scanning.Core.Scanners
{
    /// <summary>
    /// What every analyzer established about one page, accumulated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Append-only, and deliberately not keyed by <see cref="PageComponent"/>. Two analyzers may
    /// observe the same component from different surfaces — an aspx directive and a web part
    /// manager both speak to web part zones — and they may disagree. A dictionary would silently
    /// keep one; keeping both is the whole reason this type exists, because a disagreement between
    /// two readings is itself a finding.
    /// </para>
    /// <para>
    /// Nothing here narrows the page. A component no analyzer reported is simply absent from the
    /// report, which is not the same as having been looked for and not found: that is
    /// <see cref="PageComponentOutcome.Absent"/>, and it is recorded.
    /// </para>
    /// </remarks>
    internal sealed class PageCompositionReport : IReadOnlyCollection<PageComponentObservation>
    {
        private readonly List<PageComponentObservation> observations = new();

        public int Count => observations.Count;

        internal void Add(PageComponentObservation observation)
        {
            ArgumentNullException.ThrowIfNull(observation);
            observations.Add(observation);
        }

        /// <summary>Every observation of a component, in the order the analyzers ran.</summary>
        internal IReadOnlyList<PageComponentObservation> For(PageComponent component) =>
            observations.Where(value => value.Component == component).ToArray();

        /// <summary>
        /// The single observation of a component, or <c>null</c> when none was made. Throws when a
        /// component was observed more than once, because a caller that wants one answer must say
        /// what to do about two.
        /// </summary>
        internal PageComponentObservation Single(PageComponent component)
        {
            var found = For(component);
            return found.Count switch
            {
                0 => null,
                1 => found[0],
                _ => throw new InvalidOperationException(
                    $"{component} was observed {found.Count} times; the caller must reconcile them explicitly."),
            };
        }

        /// <summary>
        /// Whether a component was established as present by any analyzer. False covers absent, not
        /// applicable, unreadable and unobserved alike, so it is only safe where those four really
        /// do mean the same thing to the caller.
        /// </summary>
        internal bool IsPresent(PageComponent component) =>
            observations.Any(value => value.Component == component &&
                value.Outcome == PageComponentOutcome.Present);

        public IEnumerator<PageComponentObservation> GetEnumerator() => observations.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
