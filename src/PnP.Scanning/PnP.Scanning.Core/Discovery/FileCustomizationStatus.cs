using Microsoft.SharePoint.Client;

namespace PnP.Scanning.Core.Discovery;

internal static class FileCustomizationStatus
{
    internal static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (Enum.TryParse<CustomizedPageStatus>(value.Trim(), ignoreCase: true, out var status) &&
            Enum.IsDefined(status))
        {
            return status.ToString();
        }

        throw new InvalidDataException("SharePoint returned an unrecognized file customization status.");
    }
}
