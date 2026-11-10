namespace PnP.Scanning.Core.Pipeline.Contracts.Module;

/// <summary>
/// Pins object acquisition shape and Page-identity semantics independently of public CLI modes or database schema versions.
/// </summary>
internal static class ClassicContentContract
{
    internal const string ModuleKey = "classicpage";
    internal const string InputVersion = "classic-objects-v3";
    internal const string RuleVersion = "classic-pages-v3";
}