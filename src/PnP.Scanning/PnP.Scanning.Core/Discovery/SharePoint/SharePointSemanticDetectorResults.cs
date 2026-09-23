namespace PnP.Scanning.Core.Discovery;

internal static class SharePointSemanticDetectorResults
{
    internal const string None = "none";
    internal const string LoginShell = "login-shell";
    internal const string AccessDenied = "access-denied";
    internal const string Unauthorized = "unauthorized";
    internal const string ErrorEnvelope = "error-envelope";

    internal static bool IsKnown(string value) => value is None or LoginShell or AccessDenied or
        Unauthorized or ErrorEnvelope;
}
