namespace PnP.Scanning.Core.Scanners.Extensions;

public static class StringExtensions
{
    public static bool ContainsIgnoringCasing(this string value, string comparedWith, StringComparison stringComparison = StringComparison.InvariantCultureIgnoreCase)
    {
        return value.IndexOf(comparedWith, stringComparison) >= 0;
    }
}