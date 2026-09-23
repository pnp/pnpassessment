namespace PnP.Scanning.Core.Discovery;

internal static class AspxAdmission
{
    internal static AspxAdmissionResult Evaluate(RawDiscoveryRecord record)
    {
        var leaf = record.FileName;
        if (string.IsNullOrWhiteSpace(leaf) && record.LocatorIsGuaranteedPhysicalFilePath &&
            !string.IsNullOrWhiteSpace(record.PhysicalLocator))
        {
            leaf = record.PhysicalLocator.Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        }
        if (string.IsNullOrWhiteSpace(leaf))
        {
            return new(false, null, DiscoveryGapCodes.FilenameMissing,
                "No file leaf name or guaranteed physical-file locator fallback was supplied.");
        }
        return new(string.Equals(Path.GetExtension(leaf), ".aspx", StringComparison.OrdinalIgnoreCase), leaf);
    }
}
