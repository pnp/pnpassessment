using FluentAssertions;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Scanners.WebPartMapping;
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

        // --- T5a: embedded media (images / iframes) split out as their own mappable parts ----------------

        // A one-row, one-column layout with text, an embedded image, then more text.
        private const string ImageInWikiHtml = @"
            <div class=""ExternalClassABC"">
              <table id=""layoutsTable"">
                <tbody>
                  <tr>
                    <td>
                      <div class=""ms-rte-layoutszone-outer"">
                        <div class=""ms-rte-layoutszone-inner"">
                          <p>Before the picture</p>
                          <img src=""https://contoso.example/sites/team/SiteAssets/diagram.png"" alt=""Architecture diagram"" />
                          <p>After the picture</p>
                        </div>
                      </div>
                    </td>
                  </tr>
                </tbody>
              </table>
            </div>";

        // A one-row, one-column layout with text followed by an embedded iframe (e.g. a YouTube embed).
        private const string VideoInWikiHtml = @"
            <div class=""ExternalClassABC"">
              <table id=""layoutsTable"">
                <tbody>
                  <tr>
                    <td>
                      <div class=""ms-rte-layoutszone-outer"">
                        <div class=""ms-rte-layoutszone-inner"">
                          <p>Watch this:</p>
                          <iframe src=""https://www.youtube.com/embed/dQw4w9WgXcQ"" width=""640"" height=""360"" allowfullscreen=""true""></iframe>
                        </div>
                      </div>
                    </td>
                  </tr>
                </tbody>
              </table>
            </div>";

        // An image wrapped in an anchor — the modern image web part carries the link as its anchor.
        private const string AnchorWrappedImageHtml = @"
            <div class=""ExternalClassABC"">
              <table id=""layoutsTable"">
                <tbody>
                  <tr>
                    <td>
                      <div class=""ms-rte-layoutszone-outer"">
                        <div class=""ms-rte-layoutszone-inner"">
                          <a href=""https://contoso.example/landing""><img src=""https://contoso.example/sites/team/SiteAssets/banner.png"" alt=""Banner"" /></a>
                        </div>
                      </div>
                    </td>
                  </tr>
                </tbody>
              </table>
            </div>";

        [Fact]
        public void WikiHtml_EmbeddedImage_EmitsWikiImagePart()
        {
            var result = WikiContentParser.Parse(ImageInWikiHtml);

            // The image is emitted as its own fully-resolved media part (no CSOM placeholder needed).
            result.Placeholders.Should().BeEmpty();
            result.MediaParts.Should().ContainSingle();

            var image = result.MediaParts[0];
            image.Type.Should().Be(WikiContentParser.WikiImagePartType);
            image.Row.Should().Be(1);
            image.Column.Should().Be(1);
            // Interleaved order: text (1), image (2), text (3).
            image.Order.Should().Be(2);
            image.Properties.Should().ContainKey("ImageUrl");
            image.Properties["ImageUrl"].Should().Contain("diagram.png");
            image.Properties["AlternativeText"].Should().Be("Architecture diagram");

            // The image is no longer swallowed into the surrounding wiki text...
            result.TextParts.Should().HaveCount(2);
            result.TextParts.Should().OnlyContain(t =>
                !t.Properties["Text"].Contains("diagram.png") && !t.Properties["Text"].Contains("<img"));

            // ...and the text on either side of it still survives as its own block(s).
            string.Join(" ", result.TextParts.Select(t => t.Properties["Text"]))
                .Should().Contain("Before the picture").And.Contain("After the picture");
        }

        [Fact]
        public void WikiHtml_EmbeddedIframe_EmitsWikiVideoPart()
        {
            // The PnP.Framework wiki analyzer treats an embedded iframe as a "video in wiki text" part
            // (mapped to a modern ContentEmbed); it never emits a WikiEmbedPart from the wiki walk.
            var result = WikiContentParser.Parse(VideoInWikiHtml);

            result.Placeholders.Should().BeEmpty();
            result.MediaParts.Should().ContainSingle();

            var video = result.MediaParts[0];
            video.Type.Should().Be(WikiContentParser.WikiVideoPartType);
            video.Row.Should().Be(1);
            video.Column.Should().Be(1);
            video.Properties["Source"].Should().Contain("youtube.com/embed");
            video.Properties.Should().ContainKey("IFrameEmbed");
            video.Properties["IFrameEmbed"].Should().Contain("<iframe");
            video.Properties["AllowFullScreen"].Should().Be("True");
            video.Properties["Width"].Should().Be("640");
            video.Properties["Height"].Should().Be("360");

            // The iframe markup is not retained inside any wiki text block.
            result.TextParts.Should().OnlyContain(t => !t.Properties["Text"].Contains("<iframe"));
        }

        [Fact]
        public void WikiHtml_AnchorWrappedImage_EmitsImagePartWithAnchor()
        {
            var result = WikiContentParser.Parse(AnchorWrappedImageHtml);

            result.MediaParts.Should().ContainSingle();
            var image = result.MediaParts[0];
            image.Type.Should().Be(WikiContentParser.WikiImagePartType);
            image.Properties["ImageUrl"].Should().Contain("banner.png");
            image.Properties["Anchor"].Should().Be("https://contoso.example/landing");

            // The anchor + image is fully removed from the text; no text part holds the markup (the
            // image was the only content, so there may be no surviving text part at all).
            result.TextParts
                .Where(t => t.Properties["Text"].Contains("<img") || t.Properties["Text"].Contains("banner.png"))
                .Should().BeEmpty();
        }

        [Fact]
        public void WikiHtml_EmbeddedMediaTypes_AreMappable()
        {
            // Regression tie-back to T4: the media types the parser emits must be recognized as mappable
            // by the embedded webpartmapping.xml, otherwise they would not be credited in the mapping %.
            var manager = new WebPartMappingManager();

            manager.IsMappable(WikiContentParser.WikiImagePartType).Should().BeTrue();
            manager.IsMappable(WikiContentParser.WikiVideoPartType).Should().BeTrue();
        }
    }
}
