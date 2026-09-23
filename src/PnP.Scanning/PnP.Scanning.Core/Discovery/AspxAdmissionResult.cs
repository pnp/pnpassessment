namespace PnP.Scanning.Core.Discovery;

internal sealed record AspxAdmissionResult(bool IsAspx, string LeafName, string GapCode = null, string Detail = null);
