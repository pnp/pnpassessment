namespace PnP.Scanning.Core.Discovery;

internal static class AspxSystemListFormsPolicy
{
    internal const int UserInformationListTemplate = 112;
    internal const string RuleId = "sharepoint-user-information-list-forms-http-400";
    internal const string RuleVersion = "v1";
    internal const string ReasonCode = "system_list_forms_http_400_reference_unknown";

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
        return new(RuleId, RuleVersion, DiscoveryHash.Of(material), "CCD-726+CCD-734@2026-09-12",
            platform, 400, null, ReasonCode,
            "system-or-virtual-applicability-explicit;http-400-failed;historical-virtual-unknown",
            new[]
            {
                "sealed-run:62dbcc5b-80fd-4055-b528-745f9451ef2a",
                "kb:dev.titao@4c91e3a3e0544d871c7faad9f5d029b7ce55e035",
                $"platform-build:{platform}",
            });
    }
}
