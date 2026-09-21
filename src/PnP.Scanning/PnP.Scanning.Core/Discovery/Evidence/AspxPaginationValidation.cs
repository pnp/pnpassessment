namespace PnP.Scanning.Core.Discovery;

internal sealed record AspxPaginationValidation(
    DiscoveryTerminalOutcome Outcome,
    string ChainHash,
    int OutstandingTokenCount,
    IReadOnlyList<string> GapCodes);
