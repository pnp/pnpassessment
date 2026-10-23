namespace PnP.Scanning.Core.Scanners
{
    /// <summary>
    /// One analyzer's finding about one component of one page.
    /// </summary>
    /// <param name="Component">The component this is about.</param>
    /// <param name="Outcome">What was established.</param>
    /// <param name="Evidence">
    /// What was actually read, in the form it was read — a setup path, a content type id, a field
    /// name. It is what lets a later reader re-derive the conclusion instead of trusting it.
    /// </param>
    /// <param name="Detail">
    /// Why, for an outcome that is not <see cref="PageComponentOutcome.Present"/>. An
    /// <see cref="PageComponentOutcome.Unreadable"/> observation without a reason is a gap with no
    /// explanation.
    /// </param>
    /// <param name="Value">
    /// The analyzer's own typed reading, when it has one that is richer than <paramref name="Evidence"/>.
    /// Carried so a projection can use the finding without re-parsing the evidence string, and so a
    /// fact the current report has no column for is still retained.
    /// </param>
    internal sealed record PageComponentObservation(
        PageComponent Component,
        PageComponentOutcome Outcome,
        string Evidence = null,
        string Detail = null,
        object Value = null)
    {
        internal static PageComponentObservation Present(PageComponent component, string evidence,
            object value = null) =>
            new(component, PageComponentOutcome.Present, evidence, null, value);

        internal static PageComponentObservation Absent(PageComponent component, string evidence = null,
            object value = null) =>
            new(component, PageComponentOutcome.Absent, evidence, null, value);

        internal static PageComponentObservation NotApplicable(PageComponent component, string detail) =>
            new(component, PageComponentOutcome.NotApplicable, null, detail);

        internal static PageComponentObservation Unreadable(PageComponent component, string detail) =>
            new(component, PageComponentOutcome.Unreadable, null, detail);

        /// <summary>The analyzer's typed reading, when it is of the expected type.</summary>
        internal bool TryValue<T>(out T value)
        {
            if (Value is T typed)
            {
                value = typed;
                return true;
            }

            value = default;
            return false;
        }
    }
}
