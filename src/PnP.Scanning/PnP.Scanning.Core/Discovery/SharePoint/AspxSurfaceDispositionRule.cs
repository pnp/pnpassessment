namespace PnP.Scanning.Core.Discovery;

internal sealed record AspxSurfaceDispositionRule(
    string RuleId,
    string RuleVersion,
    string RuleHash,
    string ReviewRef,
    string PlatformBinding,
    int TriggerStatusCode,
    string TriggerErrorCode,
    string ReasonCode,
    string ClassificationEffect,
    IReadOnlyList<string> EvidenceRefs)
{
    internal bool Applies(SharePointRestPage page) => page != null &&
        (int)page.StatusCode == TriggerStatusCode &&
        (string.IsNullOrWhiteSpace(TriggerErrorCode) ||
            string.Equals(page.ErrorCode, TriggerErrorCode, StringComparison.Ordinal));
}
