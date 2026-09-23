namespace PnP.Scanning.Core.Discovery;

internal static class AspxPaginationContract
{
    internal static AspxPaginationValidation Validate(IReadOnlyList<AspxPaginationPageReceipt> pages,
        DiscoveryTerminalOutcome providerOutcome)
    {
        pages ??= Array.Empty<AspxPaginationPageReceipt>();
        var gaps = new List<string>();
        var outstanding = new HashSet<string>(StringComparer.Ordinal);
        string endpoint = null;
        string nextExpectedRequest = null;

        for (var index = 0; index < pages.Count; index++)
        {
            var page = pages[index];
            if (page.ReceiptVersion != AspxAcquisitionVersions.PaginationReceipt)
                gaps.Add("pagination_receipt_version_incompatible");
            if (page.PageOrdinal != index) gaps.Add("pagination_ordinal_integrity");
            if (!string.Equals(page.ActualMethod, "GET", StringComparison.Ordinal))
                gaps.Add("pagination_actual_method_invalid");
            if (string.IsNullOrWhiteSpace(page.ActualEndpoint)) gaps.Add("pagination_actual_endpoint_missing");
            else if (AspxDurableRequestEvidence.ContainsRawContinuationValue(page.ActualEndpoint))
                gaps.Add("pagination_raw_continuation_value_persisted");
            if (!SharePointSemanticDetectorResults.IsKnown(page.SemanticDetectorResult))
                gaps.Add("pagination_semantic_result_invalid");
            if (page.AttemptLimit < 1 || page.AttemptCount < 1 || page.AttemptCount > page.AttemptLimit)
                gaps.Add("pagination_attempt_bound_invalid");
            if (page.ReceivedAtUtc == default) gaps.Add("pagination_received_utc_missing");
            if (string.IsNullOrWhiteSpace(page.ResponseDigest)) gaps.Add("pagination_response_digest_missing");
            if (page.HttpStatusCode == null && !string.Equals(page.ErrorCode, "transport_failure", StringComparison.Ordinal))
                gaps.Add("pagination_http_status_missing_without_transport_error");
            endpoint ??= page.ActualEndpointHash;
            if (!string.Equals(endpoint, page.ActualEndpointHash, StringComparison.Ordinal))
                gaps.Add("pagination_endpoint_drift");
            if (!string.Equals(nextExpectedRequest, page.RequestTokenHash, StringComparison.Ordinal))
                gaps.Add("pagination_token_binding");
            if (!string.IsNullOrWhiteSpace(page.RequestTokenHash)) outstanding.Remove(page.RequestTokenHash);
            if (!string.IsNullOrWhiteSpace(page.NextTokenHash))
            {
                if (!outstanding.Add(page.NextTokenHash)) gaps.Add("pagination_token_loop_or_loss");
                if (page.TerminalFlag) gaps.Add("terminal_page_returned_token");
            }
            nextExpectedRequest = page.NextTokenHash;
            if (page.TerminalFlag && index != pages.Count - 1) gaps.Add("pagination_after_terminal");
        }

        if (pages.Count == 0) gaps.Add("pagination_receipt_missing");
        else if (!pages[^1].TerminalFlag) gaps.Add("pagination_terminal_missing");
        if (outstanding.Count > 0) gaps.Add("pagination_outstanding_token");

        var canonical = string.Join("\n", pages.Select(page => string.Join('|', page.CollectionScopeKey,
            page.AuthorityRevision, page.ReceiptVersion, page.ActualEndpointHash, page.ActualMethod,
            page.ActualEndpoint, page.ActualSelect, page.ActualFilter, page.PageOrdinal, page.RequestTokenHash,
            page.ResponseItemCount, page.NextTokenHash, page.ResponseDigest, page.HttpStatusCode,
            page.SemanticDetectorResult, page.AttemptCount, page.AttemptLimit, page.RequestId,
            page.CorrelationId, page.ErrorCode, page.TerminalFlag, page.ReceivedAtUtc.ToUniversalTime().ToString("O"))));
        var outcome = gaps.Count == 0 ? providerOutcome : DiscoveryTerminalOutcome.Unknown;
        return new(outcome, DiscoveryHash.Of(canonical), outstanding.Count,
            gaps.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }

    internal static string TokenHash(string token) =>
        string.IsNullOrWhiteSpace(token) ? null : DiscoveryHash.Of(token);
}
