namespace PnP.Scanning.Core.Discovery;

internal sealed record AspxReferenceCandidate(
    string SourceKind,
    string SourceObjectId,
    string AcquisitionMethod,
    string ReferenceId,
    string RawLocator,
    string CanonicalRequestPath,
    string MatchedAlias,
    string Disposition,
    string ReasonCode,
    string LinkedFileUniqueId,
    string ContentOrigin,
    string PermissionContext,
    IReadOnlyList<string> EvidenceRefs);
