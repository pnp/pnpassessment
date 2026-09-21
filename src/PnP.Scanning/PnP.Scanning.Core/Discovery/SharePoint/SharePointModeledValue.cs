namespace PnP.Scanning.Core.Discovery;

internal sealed record SharePointModeledValue(
    DiscoveryTerminalOutcome Outcome,
    string Value,
    string Provider,
    string Operation,
    string ErrorCode,
    string EvidenceRef,
    DateTimeOffset ReceivedAtUtc);
