using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PnP.Scanning.Core.Discovery.M1;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery.M1;

public sealed class AspxHandlerParserTests
{
    /// <summary>
    /// Parses complete UTF-8 file bytes so lexer cases exercise source evidence rather than rendered HTML.
    /// </summary>
    private static AspxHandlerEvidence Parse(string text) => AspxHandlerParser.Parse(
        Encoding.UTF8.GetBytes(text), AspxHandlerSourceKind.ActualFileBytes, true);

    /// <summary>
    /// Checks that unusable evidence retains its reason and optional raw declaration without claiming an effective handler.
    /// </summary>
    private static void AssertUnknown(AspxHandlerEvidence result, AspxHandlerParseState state, string expectedRawDeclaration = null)
    {
        Assert.Equal(state, result.ParseState);
        Assert.Equal(expectedRawDeclaration, result.DeclaredInherits);
        Assert.Null(result.NormalizedInherits);
        Assert.Null(result.EffectiveInherits);
        Assert.Equal(AspxHandlerDefaultProvenance.None, result.DefaultProvenance);
        Assert.NotEmpty(result.ReasonCode);
    }

    /// <summary>
    /// Accepts Page directive casing, quoting, and whitespace variations while preserving the literal declared handler.
    /// </summary>
    [Theory]
    [InlineData("<%@ Page Inherits=\"Example.Handler\" %>")]
    [InlineData("<%@pAgE iNhErItS='Example.Handler'%>")]
    [InlineData("<%@\r\n Page\n Language='C#'\r\n Inherits = 'Example.Handler'\n %><html />")]
    [InlineData("<%@ Page Inherits=Example.Handler %>")]
    public void Actual_page_directive_preserves_declared_handler(string source)
    {
        var result = Parse(source);
        Assert.Equal(AspxHandlerReadState.Available, result.ReadState);
        Assert.Equal(AspxHandlerParseState.Declared, result.ParseState);
        Assert.Equal("Example.Handler", result.DeclaredInherits);
        Assert.Equal("Example.Handler", result.NormalizedInherits);
        Assert.Equal("Example.Handler", result.EffectiveInherits);
        Assert.Equal(AspxHandlerSourceKind.ActualFileBytes, result.Source);
        Assert.Equal(AspxHandlerDefaultProvenance.None, result.DefaultProvenance);
    }

    /// <summary>
    /// Keeps CLR spelling and assembly-qualified text intact; normalization removes only surrounding whitespace.
    /// </summary>
    [Fact]
    public void Normalization_only_trims_and_does_not_change_clr_case_or_load_an_assembly()
    {
        const string literal = "  Example.Nested+Page, Missing.Assembly, Version=1.2.3.4  ";
        var result = Parse("<%@ Page Inherits='" + literal + "' %>");
        Assert.Equal(literal, result.DeclaredInherits);
        Assert.Equal(literal.Trim(), result.NormalizedInherits);
        Assert.Equal(literal.Trim(), result.EffectiveInherits);
    }

    /// <summary>
    /// Decodes strict UTF-8 and BOM-marked UTF-16 in both byte orders without losing a Unicode handler name.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Strict_utf8_and_bom_utf16_variants_read_the_actual_directive(int format)
    {
        Encoding encoding = format switch
        {
            0 => new UTF8Encoding(false, true),
            1 => new UTF8Encoding(true, true),
            2 => new UnicodeEncoding(false, true, true),
            _ => new UnicodeEncoding(true, true, true)
        };
        byte[] source = encoding.GetPreamble().Concat(encoding.GetBytes("<%@ Page Inherits='Example.页面' %>")).ToArray();
        var result = AspxHandlerParser.Parse(source, AspxHandlerSourceKind.ActualFileBytes, true);
        Assert.Equal(AspxHandlerParseState.Declared, result.ParseState);
        Assert.Equal("Example.页面", result.EffectiveInherits);
    }

    /// <summary>
    /// Labels the framework fallback as an unresolved configuration assumption only after reading complete actual source.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("<html><body>Actual source with no directive</body></html>")]
    [InlineData("<%@ Page Language='C#' %>")]
    [InlineData("<%@ Import Namespace='Example' %><html />")]
    [InlineData("&lt;%@ Page Inherits='NotAnAspToken' %&gt;")]
    public void Complete_actual_source_without_page_inherits_labels_framework_default_as_an_assumption(string source)
    {
        var result = Parse(source);
        Assert.Equal(AspxHandlerParseState.NoInherits, result.ParseState);
        Assert.Null(result.DeclaredInherits);
        Assert.Null(result.NormalizedInherits);
        Assert.Equal("System.Web.UI.Page", result.EffectiveInherits);
        Assert.Equal(AspxHandlerDefaultProvenance.FrameworkDefaultAssumptionNotConfigResolved, result.DefaultProvenance);
    }

    /// <summary>
    /// Preserves unsuccessful read states and refuses to infer handlers even when supplied bytes contain a directive.
    /// </summary>
    [Theory]
    [InlineData((int)AspxHandlerReadState.Denied)]
    [InlineData((int)AspxHandlerReadState.Unavailable)]
    [InlineData((int)AspxHandlerReadState.Truncated)]
    [InlineData((int)AspxHandlerReadState.NotRequested)]
    public void A_failed_or_unrequested_read_never_gains_a_handler_or_default(int stateValue)
    {
        var state = (AspxHandlerReadState)stateValue;
        var result = AspxHandlerParser.Parse(Encoding.UTF8.GetBytes("<%@ Page Inherits='Tempting.Value' %>"),
            AspxHandlerSourceKind.ActualFileBytes, true, state);
        Assert.Equal(state, result.ReadState);
        AssertUnknown(result, AspxHandlerParseState.NotParsed);
    }

    /// <summary>
    /// Rejects incomplete transfers before parsing, including prefixes that already look like complete directives.
    /// </summary>
    [Theory]
    [InlineData("<%@ Page Language='C#' %>")]
    [InlineData("<%@ Page Inherits='Looks.Complete' %>")]
    public void Truncated_transfer_never_uses_even_a_complete_looking_prefix(string prefix)
    {
        var result = AspxHandlerParser.Parse(Encoding.UTF8.GetBytes(prefix), AspxHandlerSourceKind.ActualFileBytes, false);
        Assert.Equal(AspxHandlerReadState.Truncated, result.ReadState);
        AssertUnknown(result, AspxHandlerParseState.NotParsed);
    }

    /// <summary>
    /// Retains unsupported evidence provenance instead of treating rendered HTML or a locator as actual file bytes.
    /// </summary>
    [Theory]
    [InlineData((int)AspxHandlerSourceKind.Unknown)]
    [InlineData((int)AspxHandlerSourceKind.RenderedHtml)]
    [InlineData((int)AspxHandlerSourceKind.PathOnly)]
    public void Rendered_or_path_only_evidence_is_never_parsed_as_file_source(int sourceValue)
    {
        var source = (AspxHandlerSourceKind)sourceValue;
        var result = AspxHandlerParser.Parse(Encoding.UTF8.GetBytes("<%@ Page Inherits='Fake.Handler' %>"), source, true);
        Assert.Equal(source, result.Source);
        AssertUnknown(result, AspxHandlerParseState.UnsupportedSource);
    }

    /// <summary>
    /// Distinguishes absent bytes from a successfully read empty file, preventing an invented framework fallback.
    /// </summary>
    [Fact]
    public void Missing_bytes_are_unavailable_not_an_empty_complete_file()
    {
        var result = AspxHandlerParser.Parse(null, AspxHandlerSourceKind.ActualFileBytes, true);
        Assert.Equal(AspxHandlerReadState.Unavailable, result.ReadState);
        AssertUnknown(result, AspxHandlerParseState.NotParsed);
    }

    /// <summary>
    /// Ignores directive-shaped text inside server comments, code, and strings but still recognizes a later real directive.
    /// </summary>
    [Theory]
    [InlineData("<%-- <%@ Page Inherits='Fake.Handler' %> --%>")]
    [InlineData("<% var text = \"<%@ Page Inherits='Fake.Handler' %>\"; %>")]
    [InlineData("<% var text = @\"<%@ Page Inherits='Fake.Handler' %>\"; %>")]
    [InlineData("<% /* <%@ Page Inherits='Fake.Handler' %> */ %>")]
    [InlineData("<% // <%@ Page Inherits='Fake.Handler' %>\n %>")]
    [InlineData("<script runat='server'>var text = \"<%@ Page Inherits='Fake.Handler' %>\";</script>")]
    [InlineData("<SCRIPT RUNAT=server>var text = \"</script><%@ Page Inherits='Fake.Handler' %>\";</SCRIPT>")]
    public void Server_comments_code_and_strings_cannot_inject_page_evidence(string ignored)
    {
        var absent = Parse(ignored);
        Assert.Equal(AspxHandlerParseState.NoInherits, absent.ParseState);
        Assert.Equal(AspxHandlerDefaultProvenance.FrameworkDefaultAssumptionNotConfigResolved, absent.DefaultProvenance);
        var declared = Parse(ignored + "<%@ Page Inherits='Real.Handler' %>");
        Assert.Equal("Real.Handler", declared.DeclaredInherits);
        Assert.Equal(AspxHandlerDefaultProvenance.None, declared.DefaultProvenance);
    }

    /// <summary>
    /// Skips a directive embedded in a C# raw string so only the following source directive supplies the handler.
    /// </summary>
    [Fact]
    public void A_raw_server_string_cannot_inject_a_page_directive()
    {
        string quote = new('"', 3);
        var result = Parse("<% var text = " + quote + "<%@ Page Inherits='Fake.Handler' %>" + quote + "; %>" +
            "<%@ Page Inherits='Real.Handler' %>");
        Assert.Equal("Real.Handler", result.EffectiveInherits);
    }

    /// <summary>
    /// Separates Control and Page directives so a control type cannot become the page handler.
    /// </summary>
    [Fact]
    public void A_control_inherits_is_not_mistaken_for_page_inherits()
    {
        var result = Parse("<%@ Control Inherits='Example.Control' %><%@ Page Inherits='Example.Page' %>");
        Assert.Equal("Example.Page", result.DeclaredInherits);
    }

    /// <summary>
    /// Keeps ASP token recognition inside HTML comments distinct from server-comment suppression.
    /// </summary>
    [Fact]
    public void Html_comments_are_not_server_comments_and_do_not_hide_an_asp_token()
    {
        var result = Parse("<!-- <%@ Page Inherits='Example.Page' %> -->");
        Assert.Equal("Example.Page", result.DeclaredInherits);
    }

    /// <summary>
    /// Reports the precise malformed-source reason and withholds normalized or default handlers for ambiguous declarations.
    /// </summary>
    [Theory]
    [InlineData("<%@ Page Inherits='A'", "UnclosedDirective")]
    [InlineData("<%@ Page Inherits=\"A %>", "UnclosedOrMalformedDirectiveValue")]
    [InlineData("<%@ Page Inherits='' %>", "EmptyInherits")]
    [InlineData("<%@ Page Inherits='  ' %>", "EmptyInherits")]
    [InlineData("<%@ Page Inherits='A' inherits='B' %>", "DuplicateInherits")]
    [InlineData("<%@ Page Inherits='A' INHERITS='A' %>", "DuplicateInherits")]
    [InlineData("<%@ Page Inherits='A' %><%@ Page Inherits='A' %>", "MultiplePageDirectives")]
    [InlineData("<%@ Page %><%@ Page Inherits='A' %>", "MultiplePageDirectives")]
    [InlineData("<%@ Page Inherits='A'Junk='B' %>", "MalformedDirective")]
    [InlineData("<%@ PageX Inherits='Fake.Handler' %>", "MalformedDirective")]
    [InlineData("<%@ Page Inherits='<%$ Dynamic %>' %>", "NonLiteralInherits")]
    [InlineData("<%-- <%@ Page Inherits='A' %>", "UnclosedServerComment")]
    [InlineData("<% var text = \"unterminated", "UnclosedServerCode")]
    [InlineData("<script runat='server'>var x = 1;", "UnclosedServerScript")]
    public void Malformed_or_ambiguous_source_never_silently_defaults(string source, string reason)
    {
        var result = Parse(source);
        var expectedRaw = reason == "EmptyInherits" ? (source.Contains("'  '", StringComparison.Ordinal) ? "  " : "") : null;
        AssertUnknown(result, AspxHandlerParseState.Malformed, expectedRaw);
        Assert.Equal(reason, result.ReasonCode);
    }

    /// <summary>
    /// Invalidates otherwise plausible handler evidence when later source contains an unterminated server construct.
    /// </summary>
    [Fact]
    public void Damage_after_a_declared_directive_does_not_become_a_successful_handler_fact()
    {
        AssertUnknown(Parse("<%@ Page Inherits='Example.Page' %><%--"), AspxHandlerParseState.Malformed);
    }

    /// <summary>
    /// Supplies invalid byte sequences, unsupported BOMs, replacement characters, and unmarked UTF-16 for decoder rejection.
    /// </summary>
    public static IEnumerable<object[]> InvalidBytes()
    {
        yield return new object[] { new byte[] { 0xc3, 0x28 } }; // invalid UTF-8
        yield return new object[] { new byte[] { 0xff } };
        yield return new object[] { new byte[] { 0xff, 0xfe, 0x41 } }; // odd UTF-16
        yield return new object[] { new byte[] { 0xff, 0xfe, 0x00, 0xd8 } }; // unpaired surrogate
        yield return new object[] { new byte[] { 0xfe, 0xff, 0xd8, 0x00 } };
        yield return new object[] { new byte[] { 0xff, 0xfe, 0, 0, 0x41, 0, 0, 0 } }; // UTF-32
        yield return new object[] { new byte[] { 0, 0, 0xfe, 0xff } };
        yield return new object[] { Encoding.UTF8.GetBytes("<%@ Page %>\0") };
        yield return new object[] { Encoding.UTF8.GetBytes("<%@ Page %>\ufffd") };
        yield return new object[] { Encoding.Unicode.GetBytes("<%@ Page Inherits='Hidden.By.Nulls' %>") }; // missing BOM
    }

    /// <summary>
    /// Requires damaged or unsupported encodings to yield diagnostic evidence without a declared handler or fallback.
    /// </summary>
    [Theory]
    [MemberData(nameof(InvalidBytes))]
    public void Damaged_or_unsupported_encoding_cannot_produce_a_handler_or_default(byte[] bytes)
    {
        AssertUnknown(AspxHandlerParser.Parse(bytes, AspxHandlerSourceKind.ActualFileBytes, true),
            AspxHandlerParseState.InvalidEncoding);
    }
}
