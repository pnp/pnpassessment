namespace PnP.Scanning.Core.Discovery;

internal static class AspxSystemListFormsPolicy
{
    internal const int UserInformationListTemplate = 112;
    internal const string RuleId = "sharepoint-user-information-list-forms-http-400";
    internal const string RuleVersion = "v1";
    internal const string ReasonCode = "system_list_forms_http_400_reference_unknown";

    /// <summary>Names the rule version and the date it was reviewed.</summary>
    internal const string ReviewRef = RuleId + "/" + RuleVersion + "@2026-09-12";

    internal static AspxSurfaceDispositionRule Create(IReadOnlyDictionary<string, string> metadata,
        string rootFolder, string platformBuildRef)
    {
        if (metadata == null || !metadata.TryGetValue("baseTemplate", out var templateValue) ||
            !int.TryParse(templateValue, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var template) ||
            template != UserInformationListTemplate || string.IsNullOrWhiteSpace(rootFolder) ||
            !rootFolder.TrimEnd('/').EndsWith("/_catalogs/users", StringComparison.OrdinalIgnoreCase))
            return null;
        var platform = string.IsNullOrWhiteSpace(platformBuildRef) ? "unbound-build" : platformBuildRef;
        var material = string.Join('|', RuleId, RuleVersion, UserInformationListTemplate,
            "/_catalogs/users", 400, "http_400", platform,
            "SystemOrVirtualOnly", "Failed", "expectedCount=Unknown", "historical/system/virtual applicability explicit");
        // The review reference and the evidence list are written to discovery.csv, so they name the rule and the
        // platform build only.
        return new(RuleId, RuleVersion, DiscoveryHash.Of(material), ReviewRef,
            platform, 400, null, ReasonCode,
            "system-or-virtual-applicability-explicit;http-400-failed;historical-virtual-unknown",
            new[]
            {
                $"platform-build:{platform}",
            });
    }
}
