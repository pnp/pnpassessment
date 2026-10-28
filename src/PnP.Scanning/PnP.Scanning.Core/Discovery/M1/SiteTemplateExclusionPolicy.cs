using System.IO.Enumeration;

namespace PnP.Scanning.Core.Discovery.M1;

/// <summary>Frozen, per-scan Web-template exclusion policy. An excluded parent says nothing about its children.</summary>
internal sealed class SiteTemplateExclusionPolicy
{
    internal const string DefaultPatterns = "SPSPERS#*,APP#*";
    internal const string Cleared = "none";
    private readonly string[] patterns;

    private SiteTemplateExclusionPolicy(string[] patterns) => this.patterns = patterns;

    internal IReadOnlyList<string> Patterns => Array.AsReadOnly(patterns);
    internal string CanonicalValue => patterns.Length == 0 ? Cleared : string.Join(',', patterns);

    internal static SiteTemplateExclusionPolicy Parse(string value)
    {
        value ??= DefaultPatterns;
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Template exclusions cannot be empty. Use 'none' to clear the defaults.", nameof(value));
        var tokens = value.Split(',', StringSplitOptions.TrimEntries);
        if (tokens.Any(string.IsNullOrEmpty))
            throw new ArgumentException("Template exclusion patterns cannot contain empty entries.", nameof(value));
        if (tokens.Any(token => token.Equals(Cleared, StringComparison.OrdinalIgnoreCase)))
        {
            if (tokens.Length != 1)
                throw new ArgumentException("'none' must be the sole template exclusion value.", nameof(value));
            return new SiteTemplateExclusionPolicy(Array.Empty<string>());
        }
        if (tokens.Length > 64 || tokens.Any(token => token.Length > 128 ||
            token.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('#' or '_' or '-' or '*' or '?'))))
            throw new ArgumentException("Invalid Web-template exclusion pattern.", nameof(value));
        return new SiteTemplateExclusionPolicy(tokens.Select(token => token.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal).OrderBy(token => token, StringComparer.Ordinal).ToArray());
    }

    /// <summary>False means the Web's template was not observed, not that it was admitted or excluded.</summary>
    internal bool TryEvaluate(string template, out bool excluded)
    {
        excluded = false;
        if (string.IsNullOrWhiteSpace(template)) return false;
        excluded = patterns.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, template.Trim(), ignoreCase: true));
        return true;
    }

    internal bool IsExcluded(string template) => TryEvaluate(template, out var excluded) && excluded;
}
