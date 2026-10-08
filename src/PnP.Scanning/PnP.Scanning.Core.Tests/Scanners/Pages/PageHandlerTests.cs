using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using PnP.Core.Model.SharePoint;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Storage;
using Xunit;

namespace PnP.Scanning.Core.Tests.Scanners.Pages
{
    public class PageHandlerTests
    {
        [Theory]
        [InlineData("<%@ Page Inherits=\"Microsoft.SharePoint.WebPartPages.WikiEditPage\" %>", "Microsoft.SharePoint.WebPartPages.WikiEditPage")]
        [InlineData("<%@ pAgE Language='C#'\r\n iNhErItS = 'Acme.CustomPage' %>", "Acme.CustomPage")]
        [InlineData("<%@ Page Inherits=' Acme.CustomPage, Acme.Pages, Version=1.0.0.0 ' Language='C#' %>", "Acme.CustomPage")]
        [InlineData("<%@ Page Inherits=Acme.CustomPage %>", "Acme.CustomPage")]
        [InlineData("<%@ Page Inherits='Acme.中文Page' %>", "Acme.中文Page")]
        [InlineData("<%@ Page Inherits='Acme.Generic\u00601[[System.String, mscorlib]], Acme.Pages' %>", "Acme.Generic\u00601[[System.String, mscorlib]]")]
        public void Declaration_is_preserved_without_a_type_registry(string markup, string expected)
        {
            var result = PageDirectiveParser.Parse(markup);
            result.HandlerValue.Should().Be(expected);
            result.Status.Should().Be("Declared");
            result.TypeSource.Should().Be("Declared");
            markup.Should().Contain(result.DeclaredInherits);
        }

        [Theory]
        [InlineData("<%-- <%@ Page Inherits='Fake' %> --%>")]
        [InlineData("<!-- <%@ Page Inherits='Fake' %> -->")]
        [InlineData("<%@ Master Inherits='Fake' %><%@ Register Inherits='Fake' %>")]
        [InlineData("<div data-example=\"<%@ Page Inherits='Fake' %>\"></div>")]
        [InlineData("<script>const example = '<%@ Page Inherits=\"Fake\" %>';</script>")]
        [InlineData("<% var example = @\"<%@ Page Inherits=\"\"Fake\"\" %>\"; %>")]
        [InlineData("<% /* <%@ Page Inherits='Fake' %> */ var x = 1; %>")]
        [InlineData("<p>Inherits='Fake' &lt;%@ Page Inherits='Fake' %&gt;</p>")]
        public void Comment_code_and_other_directives_do_not_supply_the_page_type(string distraction)
        {
            var result = PageDirectiveParser.Parse(distraction + "<%@ Page Inherits='Acme.RealPage' %>");
            result.HandlerValue.Should().Be("Acme.RealPage");
        }

        [Theory]
        [InlineData("<%@ Page Inherits='' %>", "EmptyInherits")]
        [InlineData("<%@ Page Inherits='   ' %>", "EmptyInherits")]
        [InlineData("<%@ Page Inherits=', Acme.Pages' %>", "EmptyInherits")]
        [InlineData("<%@ Page Inherits='One' Inherits='Two' %>", "DuplicateInherits")]
        [InlineData("<%@ Page Inherits='One' %><%@ Page Inherits='One' %>", "DuplicatePageDirective")]
        [InlineData("<%@ Page Inherits='One' %><%@ Page Inherits='Two' %>", "DuplicatePageDirective")]
        [InlineData("<%@ Page Inherits='One %>", "MalformedDirective")]
        [InlineData("<%@ Page Inherits %>", "MalformedDirective")]
        [InlineData("<%@ Page Inherits='One'Language='C#' %>", "MalformedDirective")]
        [InlineData("<%@ Page Inherits='One'", "MalformedDirective")]
        public void Invalid_declarations_are_errors_not_framework_defaults(string markup, string code)
        {
            var result = PageDirectiveParser.Parse(markup);
            result.HandlerValue.Should().Be($"ERROR: ParseFailed ({code})");
            result.BaseType.Should().BeNull();
            result.TypeSource.Should().BeNull();
            result.ErrorDetail.Should().NotBeNullOrEmpty();
        }

        [Fact]
        public void Not_declared_is_separate_from_the_default_assumption()
        {
            var result = PageDirectiveParser.Parse("<%@ Page Language='C#' %><html><body>Hello</body></html>");
            result.Status.Should().Be("NotDeclared");
            result.HandlerValue.Should().BeNull();
            result.DeclaredInherits.Should().BeNull();
            result.BaseType.Should().Be("System.Web.UI.Page");
            result.TypeSource.Should().Be("FrameworkDefault");
            result.ConfigurationScope.Should().BeNull();
        }

        [Fact]
        public void Applicable_configuration_is_used_only_when_no_declaration_is_present()
        {
            var config = new PageBaseTypeConfiguration("Acme.ConfiguredPage", "/sites/example");
            var noDeclaration = PageDirectiveParser.Parse("<html></html>", config);
            noDeclaration.HandlerValue.Should().BeNull();
            noDeclaration.BaseType.Should().Be(config.Value);
            noDeclaration.TypeSource.Should().Be("ConfiguredDefault");
            noDeclaration.ConfigurationScope.Should().Be(config.Scope);

            var declared = PageDirectiveParser.Parse("<%@ Page Inherits='Acme.DeclaredPage' %>", config);
            declared.HandlerValue.Should().Be("Acme.DeclaredPage");
            declared.TypeSource.Should().Be("Declared");

            PageDirectiveParser.Parse("<html></html>", new("Acme.Page", null))
                .TypeSource.Should().Be("FrameworkDefault");
        }

        [Theory]
        [InlineData(null)]
        [InlineData(7)]
        public async Task An_already_produced_analysis_record_does_not_need_a_ListItem_for_handler_enrichment(int? itemId)
        {
            var page = Page("/SitePages/Home.aspx");
            page.ListItemId = itemId;
            page.PageType = PageScanComponent.PublishingPage;
            int reads = 0;
            await PageHandlerEnricher.EnrichAsync(new[] { page }, (_, _) =>
            {
                reads++;
                return Task.FromResult(Source("<%@ Page Inherits='Microsoft.SharePoint.WebPartPages.WikiEditPage' %>"));
            }, CancellationToken.None);
            reads.Should().Be(1);
            page.PageHandler.Should().Be("Microsoft.SharePoint.WebPartPages.WikiEditPage");
            page.PageType.Should().Be(PageScanComponent.PublishingPage);
            page.AssessmentStatus.Should().Be("Complete");
        }

        [Fact]
        public async Task Only_analysis_records_are_enriched_and_virtual_records_are_not_downloaded()
        {
            var physical = Page("/SitePages/Home.aspx");
            var blog = Page("/Lists/Posts/Post.aspx");
            blog.PageType = PageScanComponent.BlogPage;
            var delve = Page("/SitePages/Post.pointpub");
            int reads = 0;
            var records = new[] { physical, blog, delve };
            await PageHandlerEnricher.EnrichAsync(records, (_, _) =>
            {
                reads++;
                return Task.FromResult(Source("<%@ Page Inherits='Acme.Page' %>"));
            }, CancellationToken.None);
            records.Should().HaveCount(3);
            reads.Should().Be(1);
            blog.PageHandler.Should().BeNull();
            delve.PageHandler.Should().BeNull();
            JsonDocument.Parse(blog.PageHandlerEvidenceJson).RootElement.GetProperty("Status").GetString()
                .Should().Be("NotApplicable");

            await PageHandlerEnricher.EnrichAsync(Array.Empty<ClassicPage>(), (_, _) =>
                throw new InvalidOperationException("Discovery inventory must not invoke this reader."), CancellationToken.None);
        }

        [Fact]
        public async Task A_handler_error_keeps_the_existing_record_and_does_not_stop_the_next_page()
        {
            var first = Page("/SitePages/Denied.aspx");
            var second = Page("/SitePages/Good.aspx");
            await PageHandlerEnricher.EnrichAsync(new[] { first, second }, (page, _) =>
                Task.FromResult(page == first
                    ? new PageSourceReadResult { Failure = PageHandlerEvidence.Failure("ReadFailed", "HTTP 403", "Access denied.") }
                    : Source("<%@ Page Inherits='Acme.Page' %>")), CancellationToken.None);
            first.PageHandler.Should().Be("ERROR: ReadFailed (HTTP 403)");
            first.AssessmentStatus.Should().Be("Complete");
            second.PageHandler.Should().Be("Acme.Page");
            first.PageHandlerEvidenceJson.Should().Contain("Access denied.");
        }

        [Fact]
        public async Task Ghosted_absence_does_not_claim_an_effective_framework_default()
        {
            var page = Page("/SitePages/Ghosted.aspx");
            var source = Source("<html></html>");
            source.Source.CustomizationStatus = "Uncustomized";
            await PageHandlerEnricher.EnrichAsync(new[] { page }, (_, _) => Task.FromResult(source), CancellationToken.None);
            page.PageHandler.Should().Be("ERROR: Unavailable (GhostedSourceUnconfirmed)");
            var evidence = JsonDocument.Parse(page.PageHandlerEvidenceJson).RootElement;
            evidence.GetProperty("BaseType").ValueKind.Should().Be(JsonValueKind.Null);

            source = Source("<%@ Page Inherits='Acme.Page' %>");
            source.Source.CustomizationStatus = "Uncustomized";
            await PageHandlerEnricher.EnrichAsync(new[] { page }, (_, _) => Task.FromResult(source), CancellationToken.None);
            page.PageHandler.Should().Be("Acme.Page");
        }

        [Fact]
        public async Task Scan_cancellation_propagates_without_writing_an_error_value()
        {
            using var cancellation = new CancellationTokenSource();
            var page = Page("/SitePages/Home.aspx");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                PageHandlerEnricher.EnrichAsync(new[] { page }, (_, _) =>
                {
                    cancellation.Cancel();
                    return Task.FromCanceled<PageSourceReadResult>(cancellation.Token);
                }, cancellation.Token));
            page.PageHandler.Should().BeNull();
            page.PageHandlerEvidenceJson.Should().BeNull();
        }

        [Fact]
        public async Task Sdk_binary_download_uses_file_identity_and_does_not_request_a_ListItem()
        {
            var page = Page("/SitePages/Home.aspx");
            var (file, proxy) = FileProxy.Create(page, "<%@ Page Inherits='Acme.Page' %>");
            var result = await PnPCorePageSourceReader.ReadAsync(page, () => Task.FromResult(file), CancellationToken.None);
            result.Content.Should().Contain("Acme.Page");
            result.Failure.Should().BeNull();
            result.Source.Method.Should().Be("PnPCoreFileDownload");
            proxy.Downloads.Should().Be(1);
        }

        [Fact]
        public async Task Identity_mismatch_is_rejected_before_downloading_the_file()
        {
            var page = Page("/SitePages/Home.aspx");
            var (file, proxy) = FileProxy.Create(page, "<html></html>");
            proxy.FileId = Guid.NewGuid();
            var result = await PnPCorePageSourceReader.ReadAsync(page, () => Task.FromResult(file), CancellationToken.None);
            result.Failure.HandlerValue.Should().Be("ERROR: Unavailable (IdentityMismatch)");
            proxy.Downloads.Should().Be(0);
        }

        [Fact]
        public async Task Http_failure_is_preserved_as_a_short_error_value()
        {
            var result = await PnPCorePageSourceReader.ReadAsync(Page("/SitePages/Denied.aspx"),
                () => Task.FromException<IFile>(new HttpRequestException("Access denied.", null, HttpStatusCode.Forbidden)),
                CancellationToken.None);
            result.Failure.HandlerValue.Should().Be("ERROR: ReadFailed (HTTP 403)");
            result.Failure.ErrorDetail.Should().Be("Access denied.");
        }

        [Theory]
        [InlineData("utf8")]
        [InlineData("utf16")]
        [InlineData("utf32")]
        public void Source_decoder_preserves_unicode_declarations(string encodingName)
        {
            Encoding encoding = encodingName switch
            {
                "utf16" => new UnicodeEncoding(false, true),
                "utf32" => new UTF32Encoding(false, true),
                _ => new UTF8Encoding(true),
            };
            const string markup = "<%@ Page Inherits='Acme.中文Page' %>";
            byte[] bytes = encoding.GetPreamble().Concat(encoding.GetBytes(markup)).ToArray();
            var result = PnPCorePageSourceReader.FromBytes(bytes, new() { ExpectedLength = bytes.Length });
            result.Content.Should().Be(markup);
            result.Failure.Should().BeNull();
        }

        [Fact]
        public void Unavailable_bytes_and_error_envelopes_are_not_default_classifications()
        {
            var truncated = PnPCorePageSourceReader.FromBytes(Encoding.UTF8.GetBytes("<html>"), new() { ExpectedLength = 99 });
            truncated.Failure.HandlerValue.Should().Be("ERROR: Unavailable (IncompleteSource)");
            PnPCorePageSourceReader.FromBytes(Array.Empty<byte>(), new()).Failure.ErrorCode.Should().Be("EmptySource");
            PnPCorePageSourceReader.FromBytes(new byte[] { 0xff, 0xff, 0xff }, new())
                .Failure.ErrorCode.Should().Be("UnsupportedEncoding");
            var envelope = Encoding.UTF8.GetBytes("{\"error\":{\"message\":\"Access denied\"}}");
            PnPCorePageSourceReader.FromBytes(envelope, new() { ExpectedLength = envelope.Length })
                .Failure.ErrorCode.Should().Be("NonSourceResponse");
        }

        private static ClassicPage Page(string url) => new()
        {
            PageUrl = url,
            FileUniqueId = Guid.NewGuid(),
            PageType = PageScanComponent.WikiPage,
            AssessmentStatus = "Complete",
        };

        private static PageSourceReadResult Source(string content) => new()
        {
            Content = content,
            Source = new PageSourceEvidence { Method = "Fixture", CustomizationStatus = "Customized" },
        };

        public class FileProxy : DispatchProxy
        {
            internal Guid FileId;
            internal string Url;
            internal byte[] Bytes;
            internal int Downloads;

            internal static (IFile File, FileProxy Proxy) Create(ClassicPage page, string markup)
            {
                var file = DispatchProxy.Create<IFile, FileProxy>();
                var proxy = (FileProxy)(object)file;
                proxy.FileId = page.FileUniqueId.Value;
                proxy.Url = page.PageUrl;
                proxy.Bytes = Encoding.UTF8.GetBytes(markup);
                return (file, proxy);
            }

            protected override object Invoke(MethodInfo method, object[] args)
            {
                switch (method.Name)
                {
                    case "get_UniqueId": return FileId;
                    case "get_ServerRelativeUrl": return Url;
                    case "get_Length": return (long)Bytes.Length;
                    case "get_CustomizedPageStatus": return CustomizedPageStatus.Customized;
                    case "GetContentBytesAsync":
                        Downloads++;
                        return Task.FromResult(Bytes);
                    default: throw new InvalidOperationException($"Unexpected SDK call: {method.Name}");
                }
            }
        }
    }
}
