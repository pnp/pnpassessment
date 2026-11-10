namespace PnP.Scanning.Core.ClassicPages.Contracts.Module;

/// <summary>
/// Pins the physical-ASPX and independent-Posts scope independently of public CLI modes or database schema versions.
/// Version 4 excludes pointpub and ordinary custom-form business-item scans; earlier sealed sources require their original rules.
/// </summary>
internal static class ClassicContentContract
{
    internal const string ModuleKey = "classicpage";
    internal const string InputVersion = "classic-objects-v4";
    internal const string RuleVersion = "classic-pages-v4";
}