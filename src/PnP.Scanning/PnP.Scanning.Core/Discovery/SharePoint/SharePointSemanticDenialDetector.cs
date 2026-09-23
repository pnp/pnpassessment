using System.Text;
using System.Text.Json;

namespace PnP.Scanning.Core.Discovery;

internal static class SharePointSemanticDenialDetector
{
    private const int InspectionLimit = 131072;

    internal static SharePointSemanticDetection Detect(byte[] body, string mediaType = null)
    {
        if (body == null || body.Length == 0)
            return new(SharePointSemanticDetectorResults.None, false, null);
        var inspected = body.AsSpan(0, Math.Min(body.Length, InspectionLimit));
        var text = Encoding.UTF8.GetString(inspected);
        var trimmed = text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        var htmlLike = (mediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) ?? false) ||
            trimmed.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
        if (htmlLike)
        {
            if (ContainsAny(trimmed, "login.microsoftonline.com", "wa=wsignin1.0", "Sign in to your account",
                    "id=\"loginForm\"", "name=\"loginfmt\""))
                return new(SharePointSemanticDetectorResults.LoginShell, true, "semantic_login_shell");
            if (ContainsAny(trimmed, "Access Denied", "AccessDenied.aspx", "Sorry, you don't have access",
                    "You need permission to access this site"))
                return new(SharePointSemanticDetectorResults.AccessDenied, true, "semantic_access_denied");
            if (ContainsAny(trimmed, "401 Unauthorized", ">Unauthorized<"))
                return new(SharePointSemanticDetectorResults.Unauthorized, true, "semantic_unauthorized");
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (TryErrorEnvelope(document.RootElement, out var errorText, out var serverErrorCode))
            {
                if (ContainsAny(errorText, "access denied", "accessdenied", "does not have permissions",
                        "-2147024891"))
                    return new(SharePointSemanticDetectorResults.AccessDenied, true,
                        serverErrorCode ?? "semantic_access_denied");
                if (ContainsAny(errorText, "unauthorized", "unauthenticated", "401"))
                    return new(SharePointSemanticDetectorResults.Unauthorized, true,
                        serverErrorCode ?? "semantic_unauthorized");
                return new(SharePointSemanticDetectorResults.ErrorEnvelope, false,
                    serverErrorCode ?? "semantic_error_envelope");
            }
        }
        catch (JsonException)
        {
            // Non-JSON success bodies are handled by the normal response parser after semantic shell detection.
        }

        if (!htmlLike && trimmed.Length < 4096)
        {
            if (trimmed.StartsWith("Access denied", StringComparison.OrdinalIgnoreCase))
                return new(SharePointSemanticDetectorResults.AccessDenied, true, "semantic_access_denied");
            if (trimmed.StartsWith("Unauthorized", StringComparison.OrdinalIgnoreCase))
                return new(SharePointSemanticDetectorResults.Unauthorized, true, "semantic_unauthorized");
        }
        return new(SharePointSemanticDetectorResults.None, false, null);
    }

    private static bool TryErrorEnvelope(JsonElement root, out string errorText, out string errorCode)
    {
        foreach (var name in new[] { "error", "odata.error" })
            if (PnPContextSharePointAspxRestClient.TryProperty(root, name, out var error))
            {
                errorText = error.GetRawText();
                errorCode = PnPContextSharePointAspxRestClient.String(error, "code");
                return true;
            }
        if (PnPContextSharePointAspxRestClient.TryProperty(root, "d", out var verbose) &&
            PnPContextSharePointAspxRestClient.TryProperty(verbose, "error", out var verboseError))
        {
            errorText = verboseError.GetRawText();
            errorCode = PnPContextSharePointAspxRestClient.String(verboseError, "code");
            return true;
        }
        errorText = null;
        errorCode = null;
        return false;
    }

    private static bool ContainsAny(string value, params string[] markers) =>
        markers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));
}
