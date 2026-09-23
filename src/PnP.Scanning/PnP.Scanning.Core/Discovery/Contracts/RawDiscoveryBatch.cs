using System.Security.Cryptography;
using System.Text;

namespace PnP.Scanning.Core.Discovery;

internal sealed record RawDiscoveryBatch(
    int BatchOrdinal,
    string RequestFingerprint,
    string ResponseFingerprint,
    IReadOnlyList<RawDiscoveryRecord> Records,
    bool IsTerminal,
    DiscoveryTerminalOutcome TerminalOutcome,
    string NextCheckpoint = null,
    string GapCode = null,
    string GapDetail = null);
