using System.Text;
using PnP.Core;
using PnP.Core.Model.SharePoint;
using PnP.Core.Services;
using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.Scanners
{
    internal static class PnPCorePageSourceReader
    {
        internal static Task<PageSourceReadResult> ReadAsync(PnPContext context, ClassicPage page,
            CancellationToken cancellationToken) => ReadAsync(page,
                () => context.Web.GetFileByServerRelativeUrlAsync(page.PageUrl,
                    file => file.UniqueId, file => file.ServerRelativeUrl,
                    file => file.Length, file => file.CustomizedPageStatus), cancellationToken);

        // Tests supply the SDK file locator without a tenant or a second request implementation.
        internal static async Task<PageSourceReadResult> ReadAsync(ClassicPage page, Func<Task<IFile>> locate,
            CancellationToken cancellationToken)
        {
            var source = new PageSourceEvidence
            {
                Method = "PnPCoreFileDownload",
                ServerRelativeUrl = page.PageUrl,
            };
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = await locate().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (file == null)
                    return Failed("ReadFailed", "HTTP 404", "The source file was not found.", source);
                source.FileUniqueId = file.UniqueId;
                source.CustomizationStatus = file.CustomizedPageStatus.ToString();
                source.ExpectedLength = file.Length;
                if (file.UniqueId == Guid.Empty ||
                    (page.FileUniqueId.HasValue && page.FileUniqueId.Value != file.UniqueId) ||
                    !string.Equals(page.PageUrl, file.ServerRelativeUrl, StringComparison.OrdinalIgnoreCase))
                    return Failed("Unavailable", "IdentityMismatch", "The downloaded file does not match the analysis record.", source);

                // The SDK uses its authenticated binary-file download route, not the rendered page URL.
                byte[] bytes = await file.GetContentBytesAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return FromBytes(bytes, source);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                int? status = ex switch
                {
                    ServiceException { Error: ServiceError error } => error.HttpResponseCode,
                    HttpRequestException { StatusCode: { } http } => (int)http,
                    _ => null,
                };
                string code = status is > 0 ? $"HTTP {status}" : "ReadError";
                string detail = ex is ServiceException { Error: not null } service
                    ? service.Error.ToString() : ex.Message;
                return Failed("ReadFailed", code, detail, source);
            }
        }

        internal static PageSourceReadResult FromBytes(byte[] bytes, PageSourceEvidence source)
        {
            source.BytesRead = bytes?.Length;
            if (bytes == null || bytes.Length == 0)
                return Failed("Unavailable", "EmptySource", "The download did not provide source markup.", source);
            if (source.ExpectedLength.HasValue && source.ExpectedLength.Value != bytes.LongLength)
                return Failed("Unavailable", "IncompleteSource", "The content length differs from the source-file metadata.", source);
            try
            {
                int offset = 0;
                Encoding encoding = new UTF8Encoding(false, true);
                if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 }))
                {
                    encoding = new UTF32Encoding(false, false, true);
                    offset = 4;
                }
                else if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff }))
                {
                    encoding = new UTF32Encoding(true, false, true);
                    offset = 4;
                }
                else if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }))
                {
                    encoding = new UnicodeEncoding(false, false, true);
                    offset = 2;
                }
                else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }))
                {
                    encoding = new UnicodeEncoding(true, false, true);
                    offset = 2;
                }
                else if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
                {
                    offset = 3;
                }
                string content = encoding.GetString(bytes, offset, bytes.Length - offset);
                // SDK/REST error envelopes are not ASPX source, even if a proxy returned HTTP 200.
                if (content.TrimStart().StartsWith('{'))
                {
                    try
                    {
                        using var document = System.Text.Json.JsonDocument.Parse(content);
                        if (document.RootElement.TryGetProperty("error", out _) ||
                            document.RootElement.TryGetProperty("odata.error", out _))
                            return Failed("Unavailable", "NonSourceResponse", "The response is a service error envelope.", source);
                    }
                    catch (System.Text.Json.JsonException) { }
                }
                return new PageSourceReadResult { Content = content, Source = source };
            }
            catch (DecoderFallbackException ex)
            {
                return Failed("Unavailable", "UnsupportedEncoding", ex.Message, source);
            }
        }

        private static PageSourceReadResult Failed(string status, string code, string detail, PageSourceEvidence source) =>
            new() { Source = source, Failure = PageHandlerEvidence.Failure(status, code, detail) };
    }
}
