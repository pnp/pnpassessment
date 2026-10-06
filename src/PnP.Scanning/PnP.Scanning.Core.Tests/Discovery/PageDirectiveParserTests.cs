using FluentAssertions;
using PnP.Scanning.Core.Discovery;
using System.Text;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery;

[Trait("Category", "PageInherits")]
public sealed class PageDirectiveParserTests
{
    [Theory]
    [InlineData("<%@ Page Inherits='Synthetic.Page' %>", "Synthetic.Page")]
    [InlineData("<%@ pAgE iNhErItS=\"Synthetic.Page\" Language='C#' %>", "Synthetic.Page")]
    [InlineData("<%@PAGE Language=\"C#\" Title='Synthetic' INHERITS='CustomPage' %>", "CustomPage")]
    [InlineData("\t<%@\tPage\r\n Language = 'C#'\n Inherits\t=\r\n\"_CustomPage\"\r\n%>", "_CustomPage")]
    [InlineData("<%@ Page Inherits=\"  Synthetic.Page \t \" %>", "  Synthetic.Page \t ")]
    [InlineData("<%@ Page Inherits='Synthetic.Page,\n Synthetic.Assembly,  Version=1.2.3.4, Culture=neutral, PublicKeyToken=null' %>",
        "Synthetic.Page,\n Synthetic.Assembly,  Version=1.2.3.4, Culture=neutral, PublicKeyToken=null")]
    [InlineData("<%@ Page Title='first' Inherits='Synthetic.Page' Language='C#' %>", "Synthetic.Page")]
    [InlineData("<%@ Page Title=\"last\" Language='C#' Inherits=\"Synthetic.Page\"%>", "Synthetic.Page")]
    [InlineData("<%@ Page Inherits='Synthetic<%--literal--%>Page' %>", "Synthetic<%--literal--%>Page")]
    [InlineData("<%@ Page Inherits='Synthetic%>Page' %>", "Synthetic%>Page")]
    public async Task Valid_declarations_preserve_raw_values_and_project_without_family_resolution(string text, string raw)
    {
        var read = await AspxSourceReaderTests.Capture(Encoding.UTF8.GetBytes(text));
        var projection = PageBaseTypeProjection.Inspect(read);
        projection.Parse.Status.Should().Be(PageDirectiveParseStatus.Declared);
        projection.Parse.IsReliableAbsence.Should().BeFalse();
        projection.DeclaredInherits.Should().Be(raw);
        projection.NormalizedInherits.Should().Be(raw.Trim());
        projection.BaseType.Should().Be(raw.Trim());
        projection.TypeSource.Should().Be(PageTypeSource.Declared);
        projection.IsReliableParse.Should().BeTrue();
        var directive = projection.Parse.Directives.Should().ContainSingle().Which;
        directive.RawText.Should().Be(text.TrimStart());
        directive.Terminated.Should().BeTrue();
        directive.EvidenceTruncated.Should().BeFalse();
        var attribute = directive.Attributes.Single(value => value.Name.Equals("Inherits", StringComparison.OrdinalIgnoreCase));
        attribute.Value.Should().Be(raw);
        text.Substring(attribute.Offset, attribute.RawText.Length).Should().Be(attribute.RawText);
        // These intentionally unbound identities still have Declared projection.
        var family = PublishingLayoutTypeEvidence.Inspect(text, new());
        family.Declaration.Should().Be(raw);
        family.Decision.Should().Be("Unknown");
        family.Reason.Should().Be("UnresolvedIdentity");
        family.ParseStatus.Should().Be("Declared");
    }

    public static IEnumerable<object[]> NonDeclarations()
    {
        yield return new object[] { "<%-- < %@ is not a directive; <%@ Page Inherits='Synthetic.Fake' %> --%>" };
        yield return new object[] { "<!-- <%@ Page Inherits='Synthetic.Fake' %> -->" };
        yield return new object[] { "<%@ Master Inherits='Synthetic.Fake' %>" };
        yield return new object[] { "<%@ Register Inherits='Synthetic.Fake' TagPrefix='fake' %>" };
        yield return new object[] { "<%@ Register Src=\"<%@ Page Inherits='Synthetic.Fake' %>\" TagPrefix='fake' %>" };
        yield return new object[] { "<div data-example=\"<%@ Page Inherits='Synthetic.Fake' %>\">body</div>" };
        yield return new object[] { "<script>const example = \"<%@ Page Inherits='Synthetic.Fake' %>\";</script>" };
        yield return new object[] { "<script runat='server'>string example = \"<%@ Page Inherits='Synthetic.Fake' %>\";</script>" };
        yield return new object[] { "<style>/* <%@ Page Inherits='Synthetic.Fake' %> */</style>" };
        yield return new object[] { "<textarea><%@ Page Inherits='Synthetic.Fake' %></textarea>" };
        yield return new object[] { "<% var example = \"<%@ Page Inherits='Synthetic.Fake' %>\"; %>" };
        yield return new object[] { "<% /* <%@ Page Inherits='Synthetic.Fake' %> */ var example = 1; %>" };
        yield return new object[] { "<% // <%@ Page Inherits='Synthetic.Fake' %>\n var example = 1; %>" };
        yield return new object[] { "<div>\"<%@ Page Inherits='Synthetic.Fake' %>\"</div>" };
        yield return new object[] { "<div>&lt;%@ Page Inherits='Synthetic.Fake' %&gt;</div>" };
        yield return new object[] { "<%@ Pager Inherits='Synthetic.Fake' %>" };
    }

    [Theory]
    [MemberData(nameof(NonDeclarations))]
    public void Body_strings_comments_and_other_directives_cannot_declare_or_make_a_duplicate(string example)
    {
        var alone = PageDirectiveParser.Parse(example);
        alone.Status.Should().Be(PageDirectiveParseStatus.PageDirectiveMissing);
        alone.DeclaredInherits.Should().BeNull();
        alone.Directives.Should().NotContain(value => value.IsPage);
        var text = "<%@ Page Inherits='Synthetic.Real' %>" + example;
        var parsed = PageDirectiveParser.Parse(text);
        parsed.Status.Should().Be(PageDirectiveParseStatus.Declared);
        parsed.DeclaredInherits.Should().Be("Synthetic.Real");
        parsed.Directives.Where(value => value.IsPage).Should().ContainSingle();
        PublishingLayoutTypeEvidence.Inspect(PublishingLayoutTypeEvidenceTests.Source(PublishingLayoutTypeEvidenceTests.Root) + example, new())
            .Decision.Should().Be("Member", "body examples must not become the inherited tail-regex false duplicate");
    }

    [Fact]
    public async Task Leading_comments_bom_and_other_directive_attributes_are_trivia_not_value_rewrites()
    {
        const string text = "\uFEFF \r\n<!-- <%@ Page Inherits='Synthetic.Fake' %> -->\n" +
            "<%-- <%@ Page Inherits='Synthetic.Fake' %> --%>\n" +
            "<%@ Register TagPrefix='synthetic' Src=\"<%@ Page Inherits='Synthetic.Fake' %>\" %>\n" +
            "<%@ Page Title=\"<%@ Page Inherits='Synthetic.Fake' %>\" Inherits='  Synthetic.Real  ' %>";
        var result = PageBaseTypeProjection.Inspect(await AspxSourceReaderTests.Capture(Encoding.UTF8.GetBytes(text)));
        result.TypeSource.Should().Be(PageTypeSource.Declared);
        result.DeclaredInherits.Should().Be("  Synthetic.Real  ");
        result.BaseType.Should().Be("Synthetic.Real");
        result.Parse.Directives.Should().HaveCount(2);
        var page = result.Parse.Directives.Single(value => value.IsPage);
        result.SourceRead.Decoding.BomByteCount.Should().Be(3);
        result.SourceRead.DecodedText.Should().Be(text[1..], "acquisition consumes the UTF-8 BOM before parsing");
        page.Offset.Should().Be(text[1..].LastIndexOf("<%@ Page Title", StringComparison.Ordinal));
        result.SourceRead.DecodedText.Substring(page.Offset, page.RawText.Length).Should().Be(page.RawText);
        PageDirectiveParser.Parse(text).Directives.Single(value => value.IsPage).Offset.Should()
            .Be(text.LastIndexOf("<%@ Page Title", StringComparison.Ordinal), "the legacy string wrapper retains its own decoded-text offsets");
        page.Attributes.Single(value => value.Name == "Title").Value.Should().Be("<%@ Page Inherits='Synthetic.Fake' %>");
    }

    [Theory]
    [InlineData("<%@ Page Inherits='Synthetic.A' Inherits='Synthetic.B' %>", 1, "DuplicateAttribute")]
    [InlineData("<%@ Page Inherits='Synthetic.A' iNhErItS='Synthetic.A' %>", 1, "DuplicateAttribute")]
    [InlineData("<%@ Page Language='C#' LANGUAGE='C#' %>", 1, "DuplicateAttribute")]
    [InlineData("<%@ Page Inherits='Synthetic.A' %><%@ Page Inherits='Synthetic.A' %>", 2, "DuplicatePageDirective")]
    [InlineData("<%@ Page Inherits='Synthetic.A' %>\n<%@ pAgE Inherits='Synthetic.B' %>", 2, "DuplicatePageDirective")]
    [InlineData("<%@ Page Language='C#' %><div>body</div><%@ Page Language='C#' %>", 2, "DuplicatePageDirective")]
    [InlineData("<%@ Page Language='C#' %><%@ Page Inherits='Synthetic.A' Broken %>", 2, "DuplicatePageDirective")]
    public async Task Duplicate_attributes_and_directives_are_errors_even_when_values_are_equal(string text, int pages, string status)
    {
        var read = await AspxSourceReaderTests.Capture(Encoding.UTF8.GetBytes(text));
        var result = PageBaseTypeProjection.Inspect(read, PageBaseTypeProjectionTests.ApplicableConfiguration());
        result.Parse.Status.ToString().Should().Be(status);
        result.Parse.Reason.Should().NotBeNullOrWhiteSpace();
        result.Parse.Directives.Where(value => value.IsPage).Should().HaveCount(pages);
        result.Parse.Directives.Select(value => value.RawText).Should().OnlyContain(value => text.Contains(value, StringComparison.Ordinal));
        result.Parse.IsReliableAbsence.Should().BeFalse();
        result.TypeSource.Should().Be(PageTypeSource.Unknown);
        result.BaseType.Should().BeNull();
        read.OriginalBytes.Should().Equal(Encoding.UTF8.GetBytes(text));
        if (status == "DuplicateAttribute")
            result.Parse.Directives.Single().Attributes.Should().HaveCountGreaterThan(1);
        else result.DeclaredInherits.Should().BeNull("a conflicting directive is not selected by order");
    }

    [Theory]
    [InlineData("", "ExplicitEmptyInherits")]
    [InlineData(" \t\r\n ", "ExplicitWhitespaceInherits")]
    public async Task Explicit_empty_is_not_verified_absent_or_a_default(string raw, string reason)
    {
        var text = "<%@ Page Inherits='" + raw + "' %>";
        var result = PageBaseTypeProjection.Inspect(await AspxSourceReaderTests.Capture(Encoding.UTF8.GetBytes(text)),
            PageBaseTypeProjectionTests.ApplicableConfiguration());
        result.DeclaredInherits.Should().Be(raw);
        result.Parse.Status.Should().Be(PageDirectiveParseStatus.EmptyInherits);
        result.Parse.Reason.Should().Be(reason);
        result.Parse.Directives.Single().Attributes.Single().ValueComplete.Should().BeTrue();
        result.Parse.IsReliableAbsence.Should().BeFalse();
        result.BaseType.Should().BeNull();
        result.TypeSource.Should().Be(PageTypeSource.Unknown);
        PublishingLayoutTypeEvidence.Inspect(text, new()).Reason.Should().Be("InheritsMissing", "the inherited API remains compatible");
    }

    [Theory]
    [InlineData("<%@ Page Inherits='Synthetic.A' Broken %>", "Synthetic.A", "AttributeEqualsMissing")]
    [InlineData("<%@ Page Inherits='Synthetic.A' %", "Synthetic.A", "AttributeNameMalformed")]
    [InlineData("<%@ Page Inherits='Synthetic.A'", "Synthetic.A", "UnterminatedDirective")]
    [InlineData("<%@ Page Inherits='Synthetic.Prefix", "Synthetic.Prefix", "UnterminatedAttributeValue")]
    [InlineData("<%@ Page Inherits= %>", null, "AttributeQuotedValueMissing")]
    [InlineData("<%@ Page Inherits='Synthetic.A'Language='C#' %>", "Synthetic.A", "AttributeWhitespaceSeparatorMissing")]
    [InlineData("<%@ Page Inherits Synthetic.A %>", null, "AttributeEqualsMissing")]
    public async Task Malformed_directives_retain_raw_and_usable_attribute_fragments_without_projection(string text, string fragment, string reason)
    {
        var read = await AspxSourceReaderTests.Capture(Encoding.UTF8.GetBytes(text));
        var result = PageBaseTypeProjection.Inspect(read, PageBaseTypeProjectionTests.ApplicableConfiguration());
        result.Parse.Status.Should().Be(PageDirectiveParseStatus.MalformedDirective);
        result.Parse.Reason.Should().Be(reason);
        result.DeclaredInherits.Should().Be(fragment);
        result.Parse.Directives.Should().ContainSingle().Which.RawText.Should().Be(text);
        result.Parse.IsReliableAbsence.Should().BeFalse();
        result.TypeSource.Should().Be(PageTypeSource.Unknown);
        result.BaseType.Should().BeNull();
        read.IsReliableSource.Should().BeTrue("acquisition and parser success are separate facts");
    }

    [Theory]
    [InlineData("<%-- unfinished comment", "UnterminatedServerComment")]
    [InlineData("<!-- unfinished comment", "UnterminatedHtmlComment")]
    [InlineData("<script>const unfinished = 1;", "UnterminatedMarkupOrRawText")]
    [InlineData("<% var unfinished = \"example", "UnterminatedServerBlockOrString")]
    public async Task Lexically_uncertain_suffixes_do_not_prove_absence(string suffix, string reason)
    {
        var text = "<%@ Page Language='C#' %>" + suffix;
        var result = PageBaseTypeProjection.Inspect(await AspxSourceReaderTests.Capture(Encoding.UTF8.GetBytes(text)));
        result.Parse.Status.Should().Be(PageDirectiveParseStatus.LexicallyUncertain);
        result.Parse.Reason.Should().Be(reason);
        result.Parse.Directives.Should().ContainSingle().Which.RawText.Should().Be("<%@ Page Language='C#' %>");
        result.TypeSource.Should().Be(PageTypeSource.Unknown);
        result.BaseType.Should().BeNull();
    }

    [Fact]
    public void An_actual_late_Page_is_misplaced_not_a_body_literal_or_reliable_absence()
    {
        var parsed = PageDirectiveParser.Parse("<div>body</div><%@ Page Inherits='Synthetic.Late' %>");
        parsed.Status.Should().Be(PageDirectiveParseStatus.MisplacedPageDirective);
        parsed.DeclaredInherits.Should().Be("Synthetic.Late");
        parsed.Directives.Single().InPreamble.Should().BeFalse();
        parsed.IsReliableDeclaration.Should().BeFalse();
        parsed.IsReliableAbsence.Should().BeFalse();
    }

    [Fact]
    public void Inspection_bounds_are_explicit_and_retain_a_bounded_prefix()
    {
        var largeSource = PageDirectiveParser.Parse("<%@ Page Language='C#' %>" + new string(' ', PageDirectiveParser.MaximumSourceCharacters));
        largeSource.Status.Should().Be(PageDirectiveParseStatus.InspectionLimitExceeded);
        largeSource.Reason.Should().Be("SourceCharacterLimitExceeded");
        largeSource.Directives.Should().ContainSingle();
        var largeDirective = PageDirectiveParser.Parse("<%@ Page Inherits='" + new string('A', PageDirectiveParser.MaximumDirectiveCharacters) + "' %>");
        largeDirective.Status.Should().Be(PageDirectiveParseStatus.InspectionLimitExceeded);
        largeDirective.Directives.Single().EvidenceTruncated.Should().BeTrue();
        largeDirective.Directives.Single().RawText.Length.Should().Be(PageDirectiveParser.MaximumDirectiveCharacters);
        largeDirective.DeclaredInherits.Should().NotBeNullOrEmpty("usable prefixes are retained, not projected");
        var manyDirectives = PageDirectiveParser.Parse(string.Concat(Enumerable.Repeat("<%@ Register TagPrefix='synthetic' %>", PageDirectiveParser.MaximumDirectives)) +
            "<%@ Page Language='C#' %>");
        manyDirectives.Status.Should().Be(PageDirectiveParseStatus.InspectionLimitExceeded);
        manyDirectives.Reason.Should().Be("DirectiveCountLimitExceeded");
        manyDirectives.Directives.Should().HaveCount(PageDirectiveParser.MaximumDirectives);
        var manyAttributes = PageDirectiveParser.Parse("<%@ Page " + string.Join(' ', Enumerable.Range(0, PageDirectiveParser.MaximumAttributes + 1)
            .Select(value => "A" + value + "='synthetic'")) + " %>");
        manyAttributes.Status.Should().Be(PageDirectiveParseStatus.InspectionLimitExceeded);
        manyAttributes.Reason.Should().Be("AttributeCountLimitExceeded");
        new[] { largeSource, largeDirective, manyDirectives, manyAttributes }.Should().OnlyContain(value =>
            !value.IsReliableAbsence && !value.IsReliableDeclaration);
    }
}
