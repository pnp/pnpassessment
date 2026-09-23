using System.Net;
using System.Text.Json;

namespace PnP.Scanning.Core.Discovery;

internal sealed record SharePointRestPage(
    Uri RequestUri,
    HttpStatusCode StatusCode,
    IReadOnlyList<JsonElement> Items,
    string NextLink,
    string ResponseDigest,
    string SchemaFlavor,
    DiscoveryTerminalOutcome Outcome,
    string ErrorCode = null,
    string SemanticDetectorResult = SharePointSemanticDetectorResults.None,
    DateTimeOffset ReceivedAtUtc = default,
    int AttemptCount = 1,
    int AttemptLimit = 1,
    string RequestId = null,
    string CorrelationId = null);
