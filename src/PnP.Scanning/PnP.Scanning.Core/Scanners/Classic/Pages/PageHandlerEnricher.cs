using PnP.Scanning.Core.Storage;
using Serilog;

namespace PnP.Scanning.Core.Scanners
{
    internal static class PageHandlerEnricher
    {
        internal static async Task EnrichAsync(IEnumerable<ClassicPage> analysisPages,
            Func<ClassicPage, CancellationToken, Task<PageSourceReadResult>> readSource,
            CancellationToken cancellationToken)
        {
            foreach (var page in analysisPages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PageHandlerEvidence evidence;
                if (page.PageType == PageScanComponent.BlogPage ||
                    string.IsNullOrEmpty(page.PageUrl) || !page.PageUrl.StartsWith('/') ||
                    !page.PageUrl.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase))
                {
                    evidence = new PageHandlerEvidence { Status = "NotApplicable" };
                }
                else
                {
                    try
                    {
                        var source = await readSource(page, cancellationToken).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        evidence = source.Failure ?? PageDirectiveParser.Parse(source.Content);
                        evidence.Source = source.Source;
                        // Uncustomized pages can depend on setup markup that the download does not
                        // establish. A positive declaration is retained, but absence is not a default.
                        if (evidence.Status == "NotDeclared" &&
                            source.Source?.CustomizationStatus == "Uncustomized")
                        {
                            evidence = PageHandlerEvidence.Failure("Unavailable", "GhostedSourceUnconfirmed",
                                "No declaration was found and the effective setup markup is not confirmed.");
                            evidence.Source = source.Source;
                        }
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        evidence = PageHandlerEvidence.Failure("ReadFailed", "ReadError", ex.Message);
                    }
                }
                page.PageHandler = evidence.HandlerValue;
                page.PageHandlerEvidenceJson = evidence.ToJson();
                if (evidence.ErrorCode != null)
                    Log.Warning("Page Handler analysis failed for {PageUrl}: {Status} ({ErrorCode})",
                        page.PageUrl, evidence.Status, evidence.ErrorCode);
            }
        }
    }
}
