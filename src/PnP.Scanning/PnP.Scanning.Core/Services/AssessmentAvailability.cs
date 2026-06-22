namespace PnP.Scanning.Core.Services
{
    /// <summary>
    /// Central decision point for whether an assessment surface has been retired and may therefore no
    /// longer be <em>started</em>. Retired assessments keep all of their engine code (scanners,
    /// options, storage model, reporting and telemetry branches) so existing scan databases can still
    /// be reported on — they are simply no longer selectable at the CLI start surface.
    ///
    /// This mirrors the existing AzureACS / SharePoint Add-Ins filtering in
    /// <c>StartCommandHandler</c>, lifted into a pure, dependency-free Core helper so the gating
    /// decision is unit-testable without the Process-host CLI.
    /// </summary>
    internal static class AssessmentAvailability
    {
        /// <summary>
        /// User-facing message shown when a retired Workflow 2013 assessment mode or classic component
        /// is requested.
        /// </summary>
        internal const string WorkflowRetiredMessage =
            "The Workflow 2013 assessment has been retired and is no longer available.";

        /// <summary>
        /// True when the given assessment <see cref="Mode"/> has been retired and can no longer be
        /// started.
        /// </summary>
        internal static bool IsRetired(Mode mode) => mode == Mode.Workflow;

        /// <summary>
        /// True when the given <see cref="ClassicComponent"/> has been retired and is dropped from a
        /// Classic assessment.
        /// </summary>
        internal static bool IsRetired(ClassicComponent component) => component == ClassicComponent.Workflow;
    }
}
