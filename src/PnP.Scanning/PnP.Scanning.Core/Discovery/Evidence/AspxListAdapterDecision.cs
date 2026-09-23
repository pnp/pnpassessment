namespace PnP.Scanning.Core.Discovery;

internal sealed record AspxListAdapterDecision(
    AspxSurfaceApplicability Applicability,
    bool RawLibraryRequired,
    bool FormsAndViewsRequired,
    AspxRuntimeCounterexampleState RuntimeCounterexampleState,
    DiscoveryTerminalOutcome Outcome);
