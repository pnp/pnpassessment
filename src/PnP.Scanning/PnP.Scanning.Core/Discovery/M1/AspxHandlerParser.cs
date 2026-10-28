using System;
using System.Collections.Generic;
using System.Text;

namespace PnP.Scanning.Core.Discovery.M1;

/// <summary>
/// Pure, fail-closed lexical reader of actual ASPX source. It does not render HTML,
/// resolve web.config, load CLR types, or use a file path/catalog to infer a handler.
/// </summary>
internal static class AspxHandlerParser
{
    private static class ReasonCodes
    {
        internal const string ReadNotAvailable = "ReadNotAvailable";
        internal const string IncompleteSource = "IncompleteSource";
        internal const string ActualFileBytesRequired = "ActualFileBytesRequired";
        internal const string MissingSourceBytes = "MissingSourceBytes";
        internal const string UnsupportedUtf32 = "UnsupportedUtf32";
        internal const string DamagedOrUnmarkedEncoding = "DamagedOrUnmarkedEncoding";
        internal const string InvalidEncodedSource = "InvalidEncodedSource";
        internal const string UnclosedServerComment = "UnclosedServerComment";
        internal const string EmptyInherits = "EmptyInherits";
        internal const string MultiplePageDirectives = "MultiplePageDirectives";
        internal const string UnclosedScriptTag = "UnclosedScriptTag";
        internal const string CompleteSourceHasNoPageInherits = "CompleteSourceHasNoPageInherits";
        internal const string MalformedDirective = "MalformedDirective";
        internal const string DuplicateInherits = "DuplicateInherits";
        internal const string DuplicateDirectiveAttribute = "DuplicateDirectiveAttribute";
        internal const string UnclosedOrMalformedDirectiveValue = "UnclosedOrMalformedDirectiveValue";
        internal const string NonLiteralInherits = "NonLiteralInherits";
        internal const string UnclosedDirective = "UnclosedDirective";
        internal const string UnclosedServerScript = "UnclosedServerScript";
        internal const string UnclosedServerCode = "UnclosedServerCode";
    }

    private static class LexerTokens
    {
        internal const string PageDirective = "Page";
        internal const string InheritsAttribute = "Inherits";
        internal const string ServerCommentStart = "<%--";
        internal const string ServerCommentEnd = "--%>";
        internal const string DirectiveStart = "<%@";
        internal const string ServerCodeStart = "<%";
        internal const string ServerCodeEnd = "%>";
        internal const string ScriptStart = "<script";
        internal const string ScriptEnd = "</script";
        internal const string SelfClosingTag = "/>";
        internal const string RunAtAttribute = "runat";
        internal const string ServerValue = "server";
        internal const string LineCommentStart = "//";
        internal const string BlockCommentStart = "/*";
        internal const string BlockCommentEnd = "*/";
    }

    private const string FrameworkPage = "System.Web.UI.Page";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UnicodeEncoding StrictUtf16Le = new(false, true, true);
    private static readonly UnicodeEncoding StrictUtf16Be = new(true, true, true);
    private static readonly HashSet<string> DirectiveNames = new(StringComparer.OrdinalIgnoreCase)
    {
        LexerTokens.PageDirective, "Register", "Import", "Assembly", "OutputCache", "MasterType",
        "PreviousPageType", "Reference", "Implements", "Control", "Master", "WebService"
    };

    /// <summary>Reads only complete, strictly decoded actual source; malformed or unavailable evidence never receives a handler default.</summary>
    internal static AspxHandlerEvidence Parse(byte[] sourceBytes, AspxHandlerSourceKind source,
        bool isComplete, AspxHandlerReadState readState = AspxHandlerReadState.Available)
    {
        AspxHandlerEvidence Unknown(AspxHandlerParseState state, string reason,
            AspxHandlerReadState? read = null) => new(read ?? readState, state, null, null, null,
                source, AspxHandlerDefaultProvenance.None, reason);

        if (readState != AspxHandlerReadState.Available)
        {
            return Unknown(AspxHandlerParseState.NotParsed, ReasonCodes.ReadNotAvailable);
        }
        if (!isComplete)
        {
            return Unknown(AspxHandlerParseState.NotParsed, ReasonCodes.IncompleteSource, AspxHandlerReadState.Truncated);
        }
        if (source != AspxHandlerSourceKind.ActualFileBytes)
        {
            return Unknown(AspxHandlerParseState.UnsupportedSource, ReasonCodes.ActualFileBytesRequired);
        }
        if (sourceBytes == null)
        {
            return Unknown(AspxHandlerParseState.NotParsed, ReasonCodes.MissingSourceBytes, AspxHandlerReadState.Unavailable);
        }

        string text;
        try
        {
            // Detect UTF-32 before UTF-16's shared FF FE prefix. Never decode with replacement fallback.
            if (sourceBytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 }) ||
                sourceBytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff }))
            {
                return Unknown(AspxHandlerParseState.InvalidEncoding, ReasonCodes.UnsupportedUtf32);
            }
            if (sourceBytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }))
            {
                text = StrictUtf16Le.GetString(sourceBytes, 2, sourceBytes.Length - 2);
            }
            else if (sourceBytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }))
            {
                text = StrictUtf16Be.GetString(sourceBytes, 2, sourceBytes.Length - 2);
            }
            else
            {
                int offset = sourceBytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
                text = StrictUtf8.GetString(sourceBytes, offset, sourceBytes.Length - offset);
            }
            if (text.IndexOf('\0') >= 0 || text.IndexOf('\ufffd') >= 0)
            {
                return Unknown(AspxHandlerParseState.InvalidEncoding, ReasonCodes.DamagedOrUnmarkedEncoding);
            }
        }
        catch (DecoderFallbackException)
        {
            return Unknown(AspxHandlerParseState.InvalidEncoding, ReasonCodes.InvalidEncodedSource);
        }

        int position = 0;
        int pages = 0;
        string declared = null;
        while (position < text.Length)
        {
            if (At(text, position, LexerTokens.ServerCommentStart))
            {
                int end = text.IndexOf(LexerTokens.ServerCommentEnd, position + 4, StringComparison.Ordinal);
                if (end < 0)
                {
                    return Unknown(AspxHandlerParseState.Malformed, ReasonCodes.UnclosedServerComment);
                }
                position = end + 4;
            }
            else if (At(text, position, LexerTokens.DirectiveStart))
            {
                if (!TryReadDirective(text, ref position, out bool page, out string inherits, out string reason))
                {
                    var malformed = Unknown(AspxHandlerParseState.Malformed, reason);
                    // A fully decoded explicit empty attribute is a raw fact, not an absent declaration.
                    return page && reason == ReasonCodes.EmptyInherits ? malformed with { DeclaredInherits = inherits } : malformed;
                }
                if (page)
                {
                    if (++pages > 1)
                    {
                        return Unknown(AspxHandlerParseState.Malformed, ReasonCodes.MultiplePageDirectives);
                    }
                    declared = inherits;
                }
            }
            else if (At(text, position, LexerTokens.ServerCodeStart))
            {
                position += 2;
                if (!TrySkipCode(text, ref position, false, out string reason))
                    return Unknown(AspxHandlerParseState.Malformed, reason);
            }
            else if (At(text, position, LexerTokens.ScriptStart, true) &&
                     IsBoundary(text, position + 7))
            {
                if (!TryReadScriptTag(text, ref position, out bool server, out bool selfClosing))
                    return Unknown(AspxHandlerParseState.Malformed, ReasonCodes.UnclosedScriptTag);
                if (server && !selfClosing && !TrySkipCode(text, ref position, true, out string reason))
                    return Unknown(AspxHandlerParseState.Malformed, reason);
            }
            else
            {
                position++;
            }
        }

        if (declared != null)
        {
            return new(readState, AspxHandlerParseState.Declared, declared, declared.Trim(), declared.Trim(),
                source, AspxHandlerDefaultProvenance.None);
        }
        return new(readState, AspxHandlerParseState.NoInherits, null, null, FrameworkPage, source,
            AspxHandlerDefaultProvenance.FrameworkDefaultAssumptionNotConfigResolved, ReasonCodes.CompleteSourceHasNoPageInherits);
    }

    /// <summary>Consumes one recognized ASP.NET directive, retaining only a literal Page Inherits value and rejecting duplicate attributes.</summary>
    private static bool TryReadDirective(string text, ref int position, out bool page,
        out string inherits, out string reason)
    {
        page = false;
        inherits = null;
        reason = ReasonCodes.MalformedDirective;
        position += 3;
        White(text, ref position);
        string name = Name(text, ref position);
        if (name.Length == 0 || !DirectiveNames.Contains(name))
        {
            return false;
        }
        page = name.Equals(LexerTokens.PageDirective, StringComparison.OrdinalIgnoreCase);
        if (!IsBoundary(text, position) && !At(text, position, LexerTokens.ServerCodeEnd))
        {
            return false;
        }
        var attributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (position < text.Length)
        {
            White(text, ref position);
            if (At(text, position, LexerTokens.ServerCodeEnd))
            {
                position += 2;
                return true;
            }
            string attribute = Name(text, ref position);
            if (attribute.Length == 0)
            {
                return false;
            }
            if (!attributes.Add(attribute))
            {
                reason = page && attribute.Equals(LexerTokens.InheritsAttribute, StringComparison.OrdinalIgnoreCase)
                    ? ReasonCodes.DuplicateInherits : ReasonCodes.DuplicateDirectiveAttribute;
                return false;
            }
            White(text, ref position);
            if (position >= text.Length || text[position++] != '=')
            {
                return false;
            }
            White(text, ref position);
            if (!TryReadValue(text, ref position, true, out string value))
            {
                reason = ReasonCodes.UnclosedOrMalformedDirectiveValue;
                return false;
            }
            if (page && attribute.Equals(LexerTokens.InheritsAttribute, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    inherits = value;
                    reason = ReasonCodes.EmptyInherits;
                    return false;
                }
                if (value.Contains(LexerTokens.ServerCodeStart, StringComparison.Ordinal))
                {
                    reason = ReasonCodes.NonLiteralInherits;
                    return false;
                }
                inherits = value;
            }
            if (position < text.Length && !char.IsWhiteSpace(text[position]) && !At(text, position, LexerTokens.ServerCodeEnd))
                return false;
        }
        reason = ReasonCodes.UnclosedDirective;
        return false;
    }

    /// <summary>Consumes a script start tag and identifies server-side bodies without treating HTML boolean attributes as values.</summary>
    private static bool TryReadScriptTag(string text, ref int position, out bool server, out bool selfClosing)
    {
        server = false;
        selfClosing = false;
        position += 7;
        while (position < text.Length)
        {
            White(text, ref position);
            if (At(text, position, LexerTokens.SelfClosingTag))
            {
                position += 2;
                selfClosing = true;
                return true;
            }
            if (position < text.Length && text[position] == '>')
            {
                position++;
                return true;
            }
            string attribute = Name(text, ref position);
            if (attribute.Length == 0)
            {
                return false;
            }
            White(text, ref position);
            if (position >= text.Length || text[position] != '=')
            {
                continue; // HTML boolean attribute.
            }
            position++;
            White(text, ref position);
            if (!TryReadValue(text, ref position, false, out string value))
            {
                return false;
            }
            if (attribute.Equals(LexerTokens.RunAtAttribute, StringComparison.OrdinalIgnoreCase) &&
                value.Equals(LexerTokens.ServerValue, StringComparison.OrdinalIgnoreCase)) server = true;
        }
        return false;
    }

    /// <summary>Reads a quoted or unquoted attribute value up to the directive or tag delimiter, failing on incomplete syntax.</summary>
    private static bool TryReadValue(string text, ref int position, bool directive, out string value)
    {
        value = null;
        if (position >= text.Length)
        {
            return false;
        }
        char quote = text[position];
        if (quote is '\'' or '"')
        {
            int start = ++position;
            while (position < text.Length && text[position] != quote)
            {
                position++;
            }
            if (position == text.Length)
            {
                return false;
            }
            value = text.Substring(start, position++ - start);
            return true;
        }
        int from = position;
        while (position < text.Length && !char.IsWhiteSpace(text[position]) &&
               !(directive ? At(text, position, LexerTokens.ServerCodeEnd) : text[position] == '>' || At(text, position, LexerTokens.SelfClosingTag)))
        {
            if (text[position] is '<' or '\'' or '"')
            {
                return false;
            }
            position++;
        }
        if (position == from)
        {
            return false;
        }
        value = text.Substring(from, position - from);
        return true;
    }

    // Code is skipped, never interpreted. Quotes/comments cannot inject a Page directive.
    // Unsupported/unclosed code constructs remain Unknown rather than granting a default.
    /// <summary>Skips server code without interpreting it; strings and comments cannot inject a Page directive or justify a default.</summary>
    private static bool TrySkipCode(string text, ref int position, bool script, out string reason)
    {
        reason = script ? ReasonCodes.UnclosedServerScript : ReasonCodes.UnclosedServerCode;
        while (position < text.Length)
        {
            if (!script && At(text, position, LexerTokens.ServerCodeEnd))
            {
                position += 2;
                return true;
            }
            if (script && At(text, position, LexerTokens.ScriptEnd, true) && IsBoundary(text, position + 8))
            {
                position += 8;
                White(text, ref position);
                if (position >= text.Length || text[position] != '>')
                {
                    return false;
                }
                position++;
                return true;
            }
            if (At(text, position, LexerTokens.LineCommentStart))
            {
                Line(text, ref position);
            }
            else if (At(text, position, LexerTokens.BlockCommentStart))
            {
                int end = text.IndexOf(LexerTokens.BlockCommentEnd, position + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    return false;
                }
                position = end + 2;
            }
            else if (text[position] == '"')
            {
                if (!TrySkipString(text, ref position))
                {
                    return false;
                }
            }
            else if (text[position] == '\'')
            {
                // C# char literal, or a VB line comment when no closing quote exists on this line.
                int end = position + 1;
                bool closed = false;
                while (end < text.Length && text[end] is not '\r' and not '\n')
                {
                    if (text[end] == '\\')
                    {
                        end += 2;
                        continue;
                    }
                    if (text[end++] == '\'')
                    {
                        closed = true;
                        break;
                    }
                }
                if (closed)
                {
                    position = end;
                }
                else
                {
                    Line(text, ref position);
                }
            }
            else
            {
                position++;
            }
        }
        return false;
    }

    /// <summary>Advances over raw, escaped, or doubled-quote strings so embedded markup remains code rather than directive evidence.</summary>
    private static bool TrySkipString(string text, ref int position)
    {
        int count = 0;
        while (position + count < text.Length && text[position + count] == '"')
        {
            count++;
        }
        if (count >= 3) // C# raw (also interpolated raw) string delimiter.
        {
            position += count;
            string end = new('"', count);
            int closing = text.IndexOf(end, position, StringComparison.Ordinal);
            if (closing < 0)
            {
                return false;
            }
            position = closing + count;
            return true;
        }
        position++;
        while (position < text.Length)
        {
            if (text[position] == '\\')
            {
                position += 2;
                continue;
            }
            if (text[position] == '"')
            {
                if (position + 1 < text.Length && text[position + 1] == '"')
                {
                    position += 2;
                    continue;
                }
                position++;
                return true;
            }
            position++;
        }
        return false;
    }

    /// <summary>Tests a source token at the current cursor using ordinal comparison and an optional case-insensitive match.</summary>
    private static bool At(string text, int position, string value, bool ignoreCase = false) =>
        position <= text.Length - value.Length && string.Compare(text, position, value, 0, value.Length,
            ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) == 0;

    /// <summary>Recognizes end-of-input, whitespace, and tag boundaries after a candidate markup name.</summary>
    private static bool IsBoundary(string text, int position) => position >= text.Length ||
        char.IsWhiteSpace(text[position]) || text[position] is '>' or '/';

    /// <summary>Advances the shared cursor over whitespace without consuming the next lexical token.</summary>
    private static void White(string text, ref int position)
    {
        while (position < text.Length && char.IsWhiteSpace(text[position]))
        {
            position++;
        }
    }

    /// <summary>Consumes the supported directive or attribute identifier alphabet, returning empty when no valid name begins here.</summary>
    private static string Name(string text, ref int position)
    {
        int from = position;
        if (position >= text.Length || !(char.IsLetter(text[position]) || text[position] == '_'))
        {
            return string.Empty;
        }
        while (position < text.Length && (char.IsLetterOrDigit(text[position]) || text[position] is '_' or ':' or '-'))
        {
            position++;
        }
        return text.Substring(from, position - from);
    }

    /// <summary>Skips code through the current line while leaving its line-ending characters for the outer reader.</summary>
    private static void Line(string text, ref int position)
    {
        while (position < text.Length && text[position] is not '\r' and not '\n')
        {
            position++;
        }
    }
}
