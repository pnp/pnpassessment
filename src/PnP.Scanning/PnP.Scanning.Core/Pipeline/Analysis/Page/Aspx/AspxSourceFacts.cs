using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Discovery;
#nullable enable
using System.Security.Cryptography;
using System.Text;

namespace PnP.Scanning.Core.Pipeline.Analysis.Page;

internal sealed record AspxSourceFacts(
    string Sha256, string? Handler, string Resolution, string? Declaration, string? Error);

internal static class AspxSourceParser
{
    internal const string DefaultHandler = "System.Web.UI.Page";
    internal const int MaximumSourceBytes = 4 * 1024 * 1024;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private static readonly Encoding Utf16LittleEndian = new UnicodeEncoding(false, true, true);
    private static readonly Encoding Utf16BigEndian = new UnicodeEncoding(true, true, true);

    internal static AspxSourceFacts Parse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (bytes.Length > MaximumSourceBytes)
        {
            return new(hash, null, "Unresolved", null, "SourceExceedsAnalysisLimit");
        }
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 }))
            return new(hash, null, "Unresolved", null, "UnsupportedSourceEncoding: UTF32");
        string text;
        try
        {
            text = bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })
                ? Utf16LittleEndian.GetString(bytes, 2, bytes.Length - 2)
                : bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })
                    ? Utf16BigEndian.GetString(bytes, 2, bytes.Length - 2)
                    : Utf8.GetString(bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? bytes.AsSpan(3) : bytes);
        }
        catch (DecoderFallbackException error)
        {
            return new(hash, null, "Unresolved", null, "UnsupportedSourceEncoding: " + error.Message);
        }
        if (text.Contains('\0'))
            return new(hash, null, "Unresolved", null, "BinaryOrUnsupportedSourceEncoding");

        string? declaration = null;
        Dictionary<string, string>? attributes = null;
        for (var position = 0; position < text.Length;)
        {
            var start = text.IndexOf("<%", position, StringComparison.Ordinal);
            if (start < 0) break;
            if (text.AsSpan(start).StartsWith("<%--", StringComparison.Ordinal))
            {
                var endComment = text.IndexOf("--%>", start + 4, StringComparison.Ordinal);
                if (endComment < 0) return new(hash, null, "Unresolved", declaration, "UnterminatedServerComment");
                position = endComment + 4;
                continue;
            }
            var end = EndBlock(text, start + 2);
            if (end < 0) return new(hash, null, "Unresolved", declaration, "UnterminatedServerBlock");
            position = end + 2;
            if (start + 2 >= text.Length || text[start + 2] != '@') continue;
            var body = text[(start + 3)..end].Trim();
            var nameEnd = 0;
            while (nameEnd < body.Length && !char.IsWhiteSpace(body[nameEnd])) nameEnd++;
            if (!body[..nameEnd].Equals("Page", StringComparison.OrdinalIgnoreCase)) continue;
            if (declaration != null) return new(hash, null, "Unresolved", declaration, "MultiplePageDirectives");
            declaration = text[start..position];
            if (!TryAttributes(body.AsSpan(nameEnd), out attributes))
                return new(hash, null, "Unresolved", declaration, "MalformedPageDirective");
        }
        if (attributes == null || !attributes.TryGetValue("Inherits", out var inherits))
            return new(hash, DefaultHandler, "DefaultAssumed", declaration, null);
        var handler = inherits.Split(',')[0].Trim();
        if (handler.Length == 0 || !ValidHandler(handler))
            return new(hash, null, "Unresolved", declaration, "InvalidInherits");
        return new(hash, handler, "Declared", declaration, null);
    }

    private static int EndBlock(string text, int start)
    {
        var quote = '\0';
        for (var i = start; i + 1 < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
            }
            else if (c is '"' or '\'') quote = c;
            else if (c == '%' && text[i + 1] == '>') return i;
        }
        return -1;
    }

    private static bool TryAttributes(ReadOnlySpan<char> text, out Dictionary<string, string> values)
    {
        values = new(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i == text.Length) return true;
            var start = i;
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or ':')) i++;
            if (i == start) return false;
            var name = text[start..i].ToString();
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i == text.Length || text[i++] != '=') return false;
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i == text.Length || text[i] is not ('"' or '\'')) return false;
            var quote = text[i++];
            start = i;
            while (i < text.Length && text[i] != quote) i++;
            if (i == text.Length || !values.TryAdd(name, text[start..i].ToString())) return false;
            i++;
        }
        return true;
    }

    private static bool ValidHandler(string value) =>
        value.Split(new[] { '.', '+' }).All(part => part.Length > 0 &&
            (char.IsLetter(part[0]) || part[0] == '_') &&
            part.All(c => char.IsLetterOrDigit(c) || c == '_'));
}
