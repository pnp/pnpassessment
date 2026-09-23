namespace PnP.Scanning.Core.Discovery;

internal static class AspxListApplicabilityPolicy
{
    internal static AspxListAdapterDecision Evaluate(int? actualBaseType)
    {
        if (actualBaseType == 1)
            return new(AspxSurfaceApplicability.Applicable, true, true,
                AspxRuntimeCounterexampleState.NoneObserved, DiscoveryTerminalOutcome.Complete);
        if (actualBaseType is 0 or 3 or 4 or 5)
            return new(AspxSurfaceApplicability.SystemOrVirtualOnly, false, true,
                AspxRuntimeCounterexampleState.NoneObserved, DiscoveryTerminalOutcome.Complete);
        if (actualBaseType == 2)
            return new(AspxSurfaceApplicability.NotApplicable, false, true,
                AspxRuntimeCounterexampleState.Observed, DiscoveryTerminalOutcome.Unknown);
        return new(AspxSurfaceApplicability.Unknown, false, true,
            actualBaseType == null ? AspxRuntimeCounterexampleState.Unknown : AspxRuntimeCounterexampleState.NoneObserved,
            DiscoveryTerminalOutcome.Unknown);
    }
}
