namespace PnP.Scanning.Core.Scanners
{
    /// <summary>
    /// Reads a Page directive from original ASPX markup without a ListItem or server type registry.
    /// This bounded lexer does not compile the page or observe its request-time handler.
    /// </summary>
    internal static class PageDirectiveParser
    {
        internal static PageHandlerEvidence Parse(string source, PageBaseTypeConfiguration configuration = null)
        {
            if (source == null)
                return PageHandlerEvidence.Failure("Unavailable", "MissingSource", "No source was supplied.");

            string declared = null;
            bool pageSeen = false;
            int position = 0;
            while (position < source.Length)
            {
                if (StartsWith(source, position, "<%--") || StartsWith(source, position, "<!--"))
                {
                    string end = StartsWith(source, position, "<%--") ? "--%>" : "-->";
                    int close = source.IndexOf(end, position + 4, StringComparison.Ordinal);
                    if (close < 0) return Fail("UnterminatedComment", "A source comment is not closed.", declared);
                    position = close + end.Length;
                }
                else if (StartsWith(source, position, "<%@"))
                {
                    int end = FindEnd(source, position + 3, "%>", code: false);
                    if (end < 0) return Fail("MalformedDirective", "A directive or its quoted attribute is not closed.", declared);
                    string directive = source.Substring(position + 3, end - position - 3);
                    int cursor = 0;
                    SkipWhiteSpace(directive, ref cursor);
                    string name = ReadName(directive, ref cursor);
                    if (string.Equals(name, "Page", StringComparison.OrdinalIgnoreCase))
                    {
                        if (pageSeen) return Fail("DuplicatePageDirective", "More than one Page directive was found.", declared);
                        pageSeen = true;
                        var attributes = ReadPageAttributes(directive, cursor);
                        if (attributes.ErrorCode != null) return attributes;
                        declared = attributes.DeclaredInherits;
                    }
                    position = end + 2;
                }
                else if (StartsWith(source, position, "<%"))
                {
                    int end = FindEnd(source, position + 2, "%>", code: true);
                    if (end < 0) return Fail("UnterminatedServerBlock", "A server code block is not closed.", declared);
                    position = end + 2;
                }
                else if (source[position] == '<' && position + 1 < source.Length &&
                    (char.IsLetter(source[position + 1]) || source[position + 1] is '/' or '!' or '?'))
                {
                    int end = FindEnd(source, position + 1, ">", code: false);
                    if (end < 0) return Fail("UnterminatedTag", "A markup tag or attribute is not closed.", declared);
                    int namePosition = position + 1;
                    string tag = ReadName(source, ref namePosition);
                    bool selfClosing = source[end - 1] == '/';
                    position = end + 1;
                    // A quoted directive example in a script body is not a Page declaration.
                    if (!selfClosing && string.Equals(tag, "script", StringComparison.OrdinalIgnoreCase))
                    {
                        int close = source.IndexOf("</script", position, StringComparison.OrdinalIgnoreCase);
                        if (close < 0) return Fail("UnterminatedScript", "A script element is not closed.", declared);
                        end = FindEnd(source, close + 2, ">", code: false);
                        if (end < 0) return Fail("UnterminatedScript", "A script closing tag is not closed.", declared);
                        position = end + 1;
                    }
                }
                else
                {
                    position++;
                }
            }

            if (declared != null)
            {
                string type = NormalizeType(declared);
                if (string.IsNullOrWhiteSpace(type))
                    return Fail("EmptyInherits", "The Inherits attribute does not contain a type name.", declared);
                return new PageHandlerEvidence
                {
                    DeclaredInherits = declared,
                    BaseType = type,
                    TypeSource = "Declared",
                    Status = "Declared",
                };
            }

            bool configured = configuration != null && !string.IsNullOrWhiteSpace(configuration.Value) &&
                !string.IsNullOrWhiteSpace(configuration.Scope);
            return new PageHandlerEvidence
            {
                Status = "NotDeclared",
                BaseType = configured ? configuration.Value : "System.Web.UI.Page",
                TypeSource = configured ? "ConfiguredDefault" : "FrameworkDefault",
                ConfigurationScope = configured ? configuration.Scope : null,
            };
        }

        private static PageHandlerEvidence ReadPageAttributes(string directive, int position)
        {
            string inherits = null;
            bool inheritsSeen = false;
            while (position < directive.Length)
            {
                SkipWhiteSpace(directive, ref position);
                if (position == directive.Length) break;
                string name = ReadName(directive, ref position);
                if (name.Length == 0) return Fail("MalformedDirective", "An attribute name is malformed.", inherits);
                SkipWhiteSpace(directive, ref position);
                if (position == directive.Length || directive[position++] != '=')
                    return Fail("MalformedDirective", "An attribute is missing its equals sign.", inherits);
                SkipWhiteSpace(directive, ref position);
                if (position == directive.Length) return Fail("MalformedDirective", "An attribute value is missing.", inherits);
                string value;
                if (directive[position] is '\'' or '"')
                {
                    char quote = directive[position++];
                    int start = position;
                    while (position < directive.Length && directive[position] != quote) position++;
                    if (position == directive.Length) return Fail("MalformedDirective", "An attribute quote is not closed.", inherits);
                    value = directive.Substring(start, position - start);
                    position++;
                }
                else
                {
                    int start = position;
                    while (position < directive.Length && !char.IsWhiteSpace(directive[position])) position++;
                    value = directive.Substring(start, position - start);
                }
                if (position < directive.Length && !char.IsWhiteSpace(directive[position]))
                    return Fail("MalformedDirective", "Attributes must be separated by whitespace.", inherits);
                if (string.Equals(name, "Inherits", StringComparison.OrdinalIgnoreCase))
                {
                    if (inheritsSeen)
                        return Fail("DuplicateInherits", $"The Inherits attribute is repeated: '{inherits}' and '{value}'.", inherits);
                    inheritsSeen = true;
                    inherits = value;
                    if (string.IsNullOrWhiteSpace(inherits))
                        return Fail("EmptyInherits", "The Inherits attribute is empty.", inherits);
                }
            }
            return new PageHandlerEvidence { DeclaredInherits = inherits };
        }

        private static string NormalizeType(string declared)
        {
            // A CLR generic argument can itself contain an assembly-qualified type.
            // Only a comma outside brackets separates this page type from its assembly.
            int depth = 0;
            for (int i = 0; i < declared.Length; i++)
            {
                if (declared[i] == '\\') { i++; continue; }
                if (declared[i] == '[') depth++;
                else if (declared[i] == ']') depth--;
                else if (declared[i] == ',' && depth == 0) return declared[..i].Trim();
            }
            return declared.Trim();
        }

        private static int FindEnd(string source, int position, string terminator, bool code)
        {
            char quote = '\0';
            bool verbatim = false;
            for (int i = position; i < source.Length; i++)
            {
                if (quote != '\0')
                {
                    if (code && !verbatim && source[i] == '\\') { i++; continue; }
                    if (source[i] == quote)
                    {
                        if (code && verbatim && i + 1 < source.Length && source[i + 1] == quote) i++;
                        else quote = '\0';
                    }
                    continue;
                }
                if (StartsWith(source, i, terminator)) return i;
                if (source[i] is '\'' or '"')
                {
                    quote = source[i];
                    verbatim = code && i > 0 && source[i - 1] == '@';
                }
                else if (code && StartsWith(source, i, "/*"))
                {
                    int close = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (close < 0) return -1;
                    i = close + 1;
                }
                else if (code && StartsWith(source, i, "//"))
                {
                    int close = source.IndexOf('\n', i + 2);
                    if (close < 0) return -1;
                    i = close;
                }
            }
            return -1;
        }

        private static bool StartsWith(string value, int position, string text) =>
            value.AsSpan(position).StartsWith(text.AsSpan(), StringComparison.Ordinal);

        private static void SkipWhiteSpace(string value, ref int position)
        {
            while (position < value.Length && char.IsWhiteSpace(value[position])) position++;
        }

        private static string ReadName(string value, ref int position)
        {
            int start = position;
            while (position < value.Length &&
                (char.IsLetterOrDigit(value[position]) || value[position] is '_' or ':' or '-' or '.')) position++;
            return value.Substring(start, position - start);
        }

        private static PageHandlerEvidence Fail(string code, string detail, string declared) =>
            PageHandlerEvidence.Failure("ParseFailed", code, detail, declared);
    }
}
