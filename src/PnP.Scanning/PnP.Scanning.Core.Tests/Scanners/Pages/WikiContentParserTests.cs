using FluentAssertions;
using PnP.Scanning.Core.Scanners;
using Xunit;

namespace PnP.Scanning.Core.Tests.Scanners.Pages
{
    /// <summary>
    /// T5 — the pure (no CSOM) wiki-HTML parser. These tests feed it captured <c>WikiField</c> HTML and
    /// assert the extracted web part placeholders (count + position + control id) and inline text blocks.
    /// The <c>LimitedWebPartManager</c> resolution path that turns a placeholder into a typed web part is
    /// CSOM-only and is covered by the integration test, not here.
    /// </summary>
    public class WikiContentParserTests
    {
        // A one-row, one-column wiki layout with: web part, then text, then a second web part.
        private const string TwoWebPartsWithTextHtml = @"
            <div class=""ExternalClassABC"">
              <table id=""layoutsTable"" style=""width: 100%;"">
                <tbody>
                  <tr>
                    <td style=""width: 100%;"">
                      <div class=""ms-rte-layoutszone-outer"" style=""width: 100%;"">
                        <div class=""ms-rte-layoutszone-inner"" style=""word-wrap: break-word; margin: 0px; border: 0px;"">
                          <div class=""ms-rte-wpbox"">
                            <div class=""ms-rtestate-read ms-rtestate-notify"" id=""div_11111111-1111-1111-1111-111111111111"" unselectable=""on"">&#160;</div>
                          </div>
                          <p>Hello wiki world</p>
                          <div class=""ms-rte-wpbox"">
                            <div class=""ms-rtestate-read ms-rtestate-notify"" id=""div_22222222-2222-2222-2222-222222222222"" unselectable=""on"">&#160;</div>
                          </div>
                        </div>
                      </div>
                    </td>
                  </tr>
                </tbody>
              </table>
            </div>";

        // A one-row, two-column layout, one web part in each column.
        private const string TwoColumnHtml = @"
            <div class=""ExternalClassABC"">
              <table id=""layoutsTable"">
                <tbody>
                  <tr>
                    <td style=""width: 49.95%;"">
                      <div class=""ms-rte-layoutszone-outer"">
                        <div class=""ms-rte-layoutszone-inner"">
                          <div class=""ms-rte-wpbox"">
                            <div class=""ms-rtestate-read"" id=""div_aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"">&#160;</div>
                          </div>
                        </div>
                      </div>
                    </td>
                    <td style=""width: 49.95%;"">
                      <div class=""ms-rte-layoutszone-outer"">
                        <div class=""ms-rte-layoutszone-inner"">
                          <div class=""ms-rte-wpbox"">
                            <div class=""ms-rtestate-read"" id=""div_bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"">&#160;</div>
                          </div>
                        </div>
                      </div>
                    </td>
                  </tr>
                </tbody>
              </table>
            </div>";

        // A layout table with text only — no web part boxes.
        private const string TextOnlyHtml = @"
            <div class=""ExternalClassABC"">
              <table id=""layoutsTable"">
                <tbody>
                  <tr>
                    <td>
                      <div class=""ms-rte-layoutszone-outer"">
                        <div class=""ms-rte-layoutszone-inner"">
                          <p>Just some article text, nothing embedded.</p>
                        </div>
                      </div>
                    </td>
                  </tr>
                </tbody>
              </table>
            </div>";

        [Fact]
        public void WikiHtml_Parse_ExtractsWebPartPlaceholders()
        {
            var result = WikiContentParser.Parse(TwoWebPartsWithTextHtml);

            // Two embedded web parts; the text between them becomes a single WikiText block.
            result.Placeholders.Should().HaveCount(2);

            // Both web parts live in the single row/column of the layout.
            result.Placeholders.Should().OnlyContain(p => p.Row == 1 && p.Column == 1);

            // Order is interleaved with the text block: web part (1), text (2), web part (3).
            result.Placeholders.Select(p => p.Order).Should().Equal(1, 3);

            // The control id is taken from the page HTML and rewritten to the g_ form used by
            // LimitedWebPartManager.GetByControlId.
            var first = result.Placeholders[0];
            first.Id.Should().Be("11111111-1111-1111-1111-111111111111");
            first.ControlId.Should().Be("g_11111111_1111_1111_1111_111111111111");

            result.Placeholders[1].Id.Should().Be("22222222-2222-2222-2222-222222222222");
            result.Placeholders[1].ControlId.Should().Be("g_22222222_2222_2222_2222_222222222222");

            // The inline text is captured as a WikiText part.
            result.TextParts.Should().HaveCount(1);
            result.TextParts[0].Type.Should().Be(WikiContentParser.WikiTextPartType);
            result.TextParts[0].Order.Should().Be(2);
            result.TextParts[0].Properties.Should().ContainKey("Text");
            result.TextParts[0].Properties["Text"].Should().Contain("Hello wiki world");
        }

        [Fact]
        public void WikiHtml_Parse_AssignsColumnNumbersPerLayoutColumn()
        {
            var result = WikiContentParser.Parse(TwoColumnHtml);

            result.Placeholders.Should().HaveCount(2);
            result.Placeholders.Select(p => p.Column).Should().Equal(1, 2);
            result.Placeholders.Should().OnlyContain(p => p.Row == 1);
            result.Placeholders[0].ControlId.Should().Be("g_aaaaaaaa_aaaa_aaaa_aaaa_aaaaaaaaaaaa");
            result.Placeholders[1].ControlId.Should().Be("g_bbbbbbbb_bbbb_bbbb_bbbb_bbbbbbbbbbbb");
        }

        [Fact]
        public void WikiHtml_NoWebParts_ReturnsEmpty()
        {
            var result = WikiContentParser.Parse(TextOnlyHtml);

            // No embedded web parts...
            result.Placeholders.Should().BeEmpty();

            // ...but the article text is still recorded as a WikiText block.
            result.TextParts.Should().ContainSingle();
            result.TextParts[0].Properties["Text"].Should().Contain("Just some article text");
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void WikiHtml_EmptyInput_ReturnsNothing(string html)
        {
            var result = WikiContentParser.Parse(html);

            result.Placeholders.Should().BeEmpty();
            result.TextParts.Should().BeEmpty();
        }

        [Fact]
        public void WikiHtml_NonLayoutHtml_WrapsContentAsSingleTextPart()
        {
            // Content that isn't a standard wiki layout table still records a single text block so the
            // page is not silently dropped.
            var result = WikiContentParser.Parse("<div>free standing wiki content</div>");

            result.Placeholders.Should().BeEmpty();
            result.TextParts.Should().ContainSingle();
            result.TextParts[0].Row.Should().Be(1);
            result.TextParts[0].Column.Should().Be(1);
            result.TextParts[0].Order.Should().Be(1);
        }
    }
}
