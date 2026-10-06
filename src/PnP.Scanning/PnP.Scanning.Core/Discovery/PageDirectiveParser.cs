namespace PnP.Scanning.Core.Discovery;

internal enum PageDirectiveParseStatus
{
    SourceUnavailable, EmptySource, PageDirectiveMissing, Declared, VerifiedAbsent, EmptyInherits,
    DuplicateAttribute, DuplicatePageDirective, MalformedDirective, MisplacedPageDirective,
    LexicallyUncertain, InspectionLimitExceeded,
}

internal sealed record PageDirectiveAttribute(string Name, string Value, int Offset, string RawText, bool ValueComplete);

internal sealed record PageDirectiveEvidence(string Name, int Offset, string RawText, bool InPreamble,
    bool Terminated, bool EvidenceTruncated, IReadOnlyList<PageDirectiveAttribute> Attributes, string SyntaxReason)
{
    internal bool IsPage => Name?.Equals("Page", StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>Lexical evidence, not CLR binding, configuration evaluation or source-acquisition success.</summary>
internal sealed record PageDirectiveParseResult(PageDirectiveParseStatus Status, string Reason,
    string DeclaredInherits, IReadOnlyList<PageDirectiveEvidence> Directives)
{
    internal string NormalizedInherits => DeclaredInherits?.Trim();
    internal bool IsReliableDeclaration => Status == PageDirectiveParseStatus.Declared;
    internal bool IsReliableAbsence => Status == PageDirectiveParseStatus.VerifiedAbsent;
    internal string RawPageDirectiveEvidence => string.Join("\n", Directives.Where(value => value.IsPage).Select(value => value.RawText));
    internal bool HasDynamicCompilation => Directives.Where(value => value.IsPage)
        .SelectMany(value => value.Attributes).Any(value =>
            value.Name.Equals("CodeFile", StringComparison.OrdinalIgnoreCase) || value.Name.Equals("Src", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// One bounded Page-directive reader shared by declaration/default projection and the inherited
/// family API. Only a directive in the leading source preamble can declare this file's type.
/// Body literals, quoted markup, comments, server blocks and raw-text elements are not directives.
/// Uncertain structure and inspection limits are explicit failures, never verified absence.
/// </summary>
internal static class PageDirectiveParser
{
    internal const int MaximumSourceCharacters = AspxSourceReader.MaximumCaptureBytes;
    internal const int MaximumDirectiveCharacters = 65536;
    internal const int MaximumDirectives = 128;
    internal const int MaximumAttributes = 256;

    internal static PageDirectiveParseResult Parse(string source)
    {
        var directives = new List<PageDirectiveEvidence>();
        PageDirectiveParseResult Result(PageDirectiveParseStatus status, string reason, string declaration = null) =>
            new(status, reason, declaration, directives.AsReadOnly());
        if (source == null) return Result(PageDirectiveParseStatus.SourceUnavailable, "DecodedSourceNotReturned");
        if (source.Length <= MaximumSourceCharacters && string.IsNullOrWhiteSpace(source))
            return Result(PageDirectiveParseStatus.EmptySource, "ObservedEmptyOrWhitespaceDecodedSource");
        // Scan a bounded prefix for usable evidence, but never infer anything from an uninspected suffix.
        var length = Math.Min(source.Length, MaximumSourceCharacters);
        string failure = source.Length > length ? "SourceCharacterLimitExceeded" : null;
        var limitExceeded = source.Length > length;
        var preamble = true;
        var position = 0;
        while (position < length)
        {
            if (char.IsWhiteSpace(source[position]) || preamble && source[position] == '\uFEFF') { position++; continue; }
            if (At(source, position, "<%--") || At(source, position, "<!--"))
            {
                var server = At(source, position, "<%--");
                var end = IndexOf(source, server ? "--%>" : "-->", position + 4, length);
                if (end < 0) { failure ??= server ? "UnterminatedServerComment" : "UnterminatedHtmlComment"; break; }
                position = end + (server ? 4 : 3);
                continue;
            }
            if (At(source, position, "<%@"))
            {
                if (directives.Count == MaximumDirectives) { limitExceeded = true; failure ??= "DirectiveCountLimitExceeded"; break; }
                var evidence = ReadDirective(source, position, length, preamble, out var next);
                directives.Add(evidence);
                if (evidence.EvidenceTruncated || evidence.SyntaxReason == "AttributeCountLimitExceeded")
                {
                    limitExceeded = true; failure ??= evidence.SyntaxReason ?? "DirectiveCharacterLimitExceeded";
                }
                else if (evidence.SyntaxReason != null) failure ??= evidence.SyntaxReason;
                position = next;
                if (!evidence.Terminated) break;
                continue;
            }
            if (At(source, position, "<%"))
            {
                preamble = false;
                if (!SkipServerBlock(source, ref position, length)) { failure ??= "UnterminatedServerBlockOrString"; break; }
                continue;
            }
            preamble = false;
            if (source[position] == '<' && position + 1 < length &&
                (NameStart(source[position + 1]) || source[position + 1] is '/' or '!' or '?'))
            {
                if (!SkipMarkup(source, ref position, length)) { failure ??= "UnterminatedMarkupOrRawText"; break; }
                continue;
            }
            if (source[position] is '"' or '\'' && (position == 0 || !char.IsLetterOrDigit(source[position - 1])))
            {
                if (!SkipBodyString(source, ref position, length)) { failure ??= "UnterminatedBodyString"; break; }
                continue;
            }
            position++;
        }

        var pages = directives.Where(value => value.IsPage).ToArray();
        var inherits = pages.Length == 1 ? pages[0].Attributes.Where(value =>
            value.Name.Equals("Inherits", StringComparison.OrdinalIgnoreCase)).ToArray() : Array.Empty<PageDirectiveAttribute>();
        // A single usable value or prefix can be retained even when syntax/acquisition prevents projection.
        var declaration = inherits.Length == 1 ? inherits[0].Value : null;
        if (limitExceeded) return Result(PageDirectiveParseStatus.InspectionLimitExceeded, failure, declaration);
        if (pages.Length > 1) return Result(PageDirectiveParseStatus.DuplicatePageDirective, "MultiplePageDirectives");
        if (pages.Length == 1 && pages[0].SyntaxReason != null)
            return Result(PageDirectiveParseStatus.MalformedDirective, pages[0].SyntaxReason, declaration);
        if (failure != null) return Result(PageDirectiveParseStatus.LexicallyUncertain, failure, declaration);
        if (pages.Length == 0) return Result(PageDirectiveParseStatus.PageDirectiveMissing, "PageDirectiveMissing");
        if (!pages[0].InPreamble) return Result(PageDirectiveParseStatus.MisplacedPageDirective, "PageDirectiveOutsideSourcePreamble", declaration);
        var duplicates = pages[0].Attributes.GroupBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
        if (duplicates.Length != 0) return Result(PageDirectiveParseStatus.DuplicateAttribute,
            "DuplicatePageAttribute:" + string.Join(',', duplicates), declaration);
        if (inherits.Length == 0) return Result(PageDirectiveParseStatus.VerifiedAbsent, "PageInheritsAttributeVerifiedAbsent");
        if (string.IsNullOrWhiteSpace(declaration)) return Result(PageDirectiveParseStatus.EmptyInherits,
            declaration?.Length == 0 ? "ExplicitEmptyInherits" : "ExplicitWhitespaceInherits", declaration);
        return Result(PageDirectiveParseStatus.Declared, "ExplicitPageInherits", declaration);
    }

    private static PageDirectiveEvidence ReadDirective(string text, int start, int length, bool preamble, out int next)
    {
        var bound = Math.Min(length, start + MaximumDirectiveCharacters);
        var position = start + 3;
        WhiteSpace(text, ref position, bound);
        var nameStart = position;
        if (position < bound && NameStart(text[position])) while (position < bound && NamePart(text[position])) position++;
        var name = text[nameStart..position];
        var attributesStart = position;
        char quote = '\0';
        var close = -1;
        while (position < bound)
        {
            if (quote != '\0')
            {
                if (text[position] == quote) quote = '\0';
            }
            else if (At(text, position, "%>") && position + 2 <= bound) { close = position; break; }
            else if (text[position] is '"' or '\'') quote = text[position];
            position++;
        }
        next = close < 0 ? bound : close + 2;
        var attributesEnd = close < 0 ? bound : close;
        var (attributes, reason) = ReadAttributes(text, attributesStart, attributesEnd);
        if (name.Length == 0) reason = "DirectiveNameMissing";
        if (close < 0) reason ??= quote != '\0' ? "UnterminatedAttributeValue" : "UnterminatedDirective";
        var truncated = close < 0 && bound < length;
        return new(name, start, text[start..next], preamble, close >= 0, truncated, attributes, reason);
    }

    private static (IReadOnlyList<PageDirectiveAttribute> Attributes, string Reason) ReadAttributes(string text, int start, int end)
    {
        var attributes = new List<PageDirectiveAttribute>();
        (IReadOnlyList<PageDirectiveAttribute>, string) Result(string reason) => (attributes.AsReadOnly(), reason);
        var position = start;
        while (position < end)
        {
            if (!char.IsWhiteSpace(text[position])) return Result("AttributeWhitespaceSeparatorMissing");
            WhiteSpace(text, ref position, end);
            if (position == end) break;
            if (attributes.Count == MaximumAttributes) return Result("AttributeCountLimitExceeded");
            var attributeStart = position;
            if (!NameStart(text[position])) return Result("AttributeNameMalformed");
            while (position < end && NamePart(text[position])) position++;
            var name = text[attributeStart..position];
            WhiteSpace(text, ref position, end);
            if (position == end || text[position] != '=')
            {
                attributes.Add(new(name, null, attributeStart, text[attributeStart..position], false));
                return Result("AttributeEqualsMissing");
            }
            position++;
            WhiteSpace(text, ref position, end);
            if (position == end || text[position] is not ('"' or '\''))
            {
                attributes.Add(new(name, position == end ? null : text[position..end], attributeStart, text[attributeStart..end], false));
                return Result("AttributeQuotedValueMissing");
            }
            var quote = text[position++];
            var valueStart = position;
            while (position < end && text[position] != quote) position++;
            var value = text[valueStart..position];
            var complete = position < end;
            if (complete) position++;
            attributes.Add(new(name, value, attributeStart, text[attributeStart..position], complete));
            if (!complete) return Result("UnterminatedAttributeValue");
        }
        return Result(null);
    }

    private static bool SkipServerBlock(string text, ref int position, int length)
    {
        position += 2;
        while (position < length)
        {
            if (At(text, position, "%>")) { position += 2; return position <= length; }
            if (At(text, position, "//"))
            {
                while (position < length && text[position] is not ('\r' or '\n')) position++;
            }
            else if (At(text, position, "/*"))
            {
                var end = IndexOf(text, "*/", position + 2, length);
                if (end < 0) return false;
                position = end + 2;
            }
            else if (text[position] is '"' or '\'')
            {
                if (!SkipBodyString(text, ref position, length)) return false;
            }
            else position++;
        }
        return false;
    }

    private static bool SkipBodyString(string text, ref int position, int length)
    {
        var quote = text[position++];
        while (position < length)
        {
            if (text[position] == '\\') { position = Math.Min(position + 2, length); continue; }
            if (text[position++] != quote) continue;
            // Doubled quotes cover the common verbatim/VB literal examples without compiling them.
            if (position < length && text[position] == quote) { position++; continue; }
            return true;
        }
        return false;
    }

    private static bool SkipMarkup(string text, ref int position, int length)
    {
        var start = position++;
        var closing = position < length && text[position] == '/';
        if (closing) position++;
        var nameStart = position;
        while (position < length && (NamePart(text[position]) || text[position] is ':' or '-')) position++;
        var name = text[nameStart..position];
        while (position < length)
        {
            if (text[position] is '"' or '\'')
            {
                var quote = text[position++];
                while (position < length && text[position] != quote) position++;
                if (position == length) return false;
                position++;
            }
            else if (At(text, position, "<%"))
            {
                if (!SkipServerBlock(text, ref position, length)) return false;
            }
            else if (text[position++] == '>')
            {
                var selfClosing = position >= start + 2 && text[position - 2] == '/';
                if (closing || selfClosing || !RawTextElement(name)) return true;
                // Inside script/style/textarea/xmp, directive-looking strings are body content.
                var search = position;
                while (search < length)
                {
                    var end = IndexOf(text, "</" + name, search, length, StringComparison.OrdinalIgnoreCase);
                    if (end < 0) return false;
                    var boundary = end + name.Length + 2;
                    if (boundary < length && (char.IsWhiteSpace(text[boundary]) || text[boundary] == '>'))
                    {
                        position = end;
                        return SkipMarkup(text, ref position, length);
                    }
                    search = boundary;
                }
                return false;
            }
        }
        return false;
    }

    private static bool RawTextElement(string name) => name.Equals("script", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("style", StringComparison.OrdinalIgnoreCase) || name.Equals("textarea", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("xmp", StringComparison.OrdinalIgnoreCase);

    private static int IndexOf(string text, string value, int start, int length, StringComparison comparison = StringComparison.Ordinal) =>
        start >= length ? -1 : text.IndexOf(value, start, length - start, comparison);
    private static bool At(string text, int position, string value) => text.AsSpan(position).StartsWith(value, StringComparison.Ordinal);
    private static bool NameStart(char value) => char.IsAsciiLetter(value) || value == '_';
    private static bool NamePart(char value) => NameStart(value) || char.IsAsciiDigit(value);
    private static void WhiteSpace(string text, ref int position, int length)
    {
        while (position < length && char.IsWhiteSpace(text[position])) position++;
    }
}
