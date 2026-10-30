using System.Globalization;
using System.Net;
using System.Text.Json;

namespace PnP.Scanning.Core.Discovery;

/// <summary>Reads one physical Files collection with bounded pages, size-only backoff and an explicit terminal-empty observation.</summary>
internal sealed class SharePointFolderFilePageReader
{
    internal const int InitialPageSize = 1000;
    internal const int MinimumPageSize = 200;
    private const int MiddlePageSize = 500;
    private const int MaximumRequests = 10000;
    private const string ItemLimitError = "file_page_item_limit_exceeded";
    private const string DuplicateIdentityError = "file_page_duplicate_identity";
    private const string InvalidContinuationError = "file_page_continuation_invalid";
    private const string RequestBudgetError = "file_page_request_budget_exceeded";
    private readonly ISharePointAspxRestClient client;
    private readonly Uri collectionEndpoint;
    private readonly List<SharePointRestPage> attempts = new();
    private readonly HashSet<string> observedIdentities = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> usedContinuations = new(StringComparer.Ordinal);
    private int offset;
    private int requestedPageSize = InitialPageSize;
    private int requestCount;

    /// <summary>Captures an unpaged authenticated Files endpoint; all subsequent cursors remain bound to its path and projection.</summary>
    internal SharePointFolderFilePageReader(ISharePointAspxRestClient client, Uri collectionEndpoint)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.collectionEndpoint = collectionEndpoint ?? throw new ArgumentNullException(nameof(collectionEndpoint));
        if (!collectionEndpoint.IsAbsoluteUri || !collectionEndpoint.AbsolutePath.EndsWith("/Files", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A physical Files collection endpoint is required.", nameof(collectionEndpoint));
        }
        NextEndpoint = OffsetEndpoint();
    }

    internal Uri NextEndpoint { get; private set; }
    internal int RequestedPageSize => requestedPageSize;
    internal IReadOnlyList<SharePointRestPage> Attempts => attempts;

    /// <summary>
    /// Reads the current cursor, reducing only an oversized response from 1000 to 500 to 200 rows.
    /// A reduced size persists for this collection. Missing nextLink never alone proves a capped collection complete.
    /// </summary>
    internal async Task<SharePointRestPage> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (NextEndpoint == null)
        {
            throw new InvalidOperationException("The Files collection has already reached its terminal observation.");
        }
        var cursor = NextEndpoint;
        SharePointRestPage page;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (requestCount >= MaximumRequests)
            {
                NextEndpoint = null;
                return new SharePointRestPage(cursor, 0, Array.Empty<JsonElement>(), null,
                    DiscoveryHash.Of(RequestBudgetError, requestCount.ToString(CultureInfo.InvariantCulture)),
                    "file-paging-error", DiscoveryTerminalOutcome.Unknown, RequestBudgetError,
                    ReceivedAtUtc: DateTimeOffset.UtcNow);
            }
            var request = SetQuery(cursor, "$top", requestedPageSize.ToString(CultureInfo.InvariantCulture));
            page = await client.GetPageAsync(request, cancellationToken).ConfigureAwait(false);
            attempts.Add(page);
            requestCount += Math.Max(1, page.AttemptCount);
            cancellationToken.ThrowIfCancellationRequested();
            var oversizedItems = page.Outcome == DiscoveryTerminalOutcome.Complete && page.Items.Count > requestedPageSize;
            if (!PnPContextSharePointAspxRestClient.IsResponseSizeLimit(page) && !oversizedItems)
            {
                break;
            }
            if (requestedPageSize == MinimumPageSize)
            {
                NextEndpoint = null;
                return oversizedItems ? Stop(page, DiscoveryTerminalOutcome.Truncated, ItemLimitError, discardItems: true)
                    : page with { NextLink = null };
            }
            requestedPageSize = requestedPageSize == InitialPageSize ? MiddlePageSize : MinimumPageSize;
        }
        if (page.Outcome != DiscoveryTerminalOutcome.Complete)
        {
            NextEndpoint = null;
            return page with { NextLink = null };
        }
        foreach (var item in page.Items)
        {
            var identity = SharePointAspxRestJson.ReadOptionalString(item, "UniqueId");
            if (!Guid.TryParse(identity, out var guid) || guid == Guid.Empty)
            {
                // The provider records missing physical identities; a path never becomes an invented file GUID.
                continue;
            }
            if (!observedIdentities.Add(guid.ToString("D")))
            {
                return Stop(page, DiscoveryTerminalOutcome.Unknown, DuplicateIdentityError);
            }
        }
        offset = checked(offset + page.Items.Count);
        if (!string.IsNullOrWhiteSpace(page.NextLink))
        {
            if (!SharePointAspxUrlPolicy.TryValidateContinuationUri(collectionEndpoint, page.NextLink, out var next) ||
                !usedContinuations.Add(next.AbsoluteUri) || !HasCompatibleOrder(next))
            {
                return Stop(page, DiscoveryTerminalOutcome.Unknown, InvalidContinuationError);
            }
            NextEndpoint = SetQuery(SetQuery(next, "$orderby", "Name asc"), "$top",
                requestedPageSize.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            NextEndpoint = page.Items.Count == 0 ? null : OffsetEndpoint();
        }
        return page with { NextLink = NextEndpoint?.AbsoluteUri };
    }

    /// <summary>Stops without discarding earlier observations, preserving the response identity and original digest for diagnostics.</summary>
    private SharePointRestPage Stop(SharePointRestPage page, DiscoveryTerminalOutcome outcome, string error,
        bool discardItems = false)
    {
        NextEndpoint = null;
        return page with
        {
            Outcome = outcome,
            ErrorCode = error,
            NextLink = null,
            Items = discardItems ? Array.Empty<JsonElement>() : page.Items,
        };
    }

    /// <summary>Builds a stable Name-ordered numeric offset without fabricating a server skiptoken or comparing names using client collation.</summary>
    private Uri OffsetEndpoint()
    {
        var endpoint = SetQuery(collectionEndpoint, "$orderby", "Name asc");
        endpoint = SetQuery(endpoint, "$skip", offset.ToString(CultureInfo.InvariantCulture));
        return SetQuery(endpoint, "$top", requestedPageSize.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Rejects a server continuation that explicitly changes the deterministic ordering used by the offset fallback.</summary>
    private static bool HasCompatibleOrder(Uri endpoint)
    {
        foreach (var segment in endpoint.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = segment.Split('=', 2);
            if (Uri.UnescapeDataString(parts[0]).Equals("$orderby", StringComparison.OrdinalIgnoreCase))
            {
                return parts.Length == 2 && Uri.UnescapeDataString(parts[1].Replace('+', ' '))
                    .Equals("Name asc", StringComparison.OrdinalIgnoreCase);
            }
        }
        return true;
    }

    /// <summary>Changes one paging control while preserving the original encoded alias, select and expand values byte-for-byte.</summary>
    private static Uri SetQuery(Uri endpoint, string key, string value)
    {
        var segments = endpoint.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => !Uri.UnescapeDataString(segment.Split('=', 2)[0])
                .Equals(key, StringComparison.OrdinalIgnoreCase)).ToList();
        segments.Add(key + "=" + Uri.EscapeDataString(value));
        return new Uri(endpoint.GetLeftPart(UriPartial.Path) + "?" + string.Join('&', segments));
    }
}
