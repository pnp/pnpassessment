using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Discovery;
#nullable enable
using System.Collections.Concurrent;
using PnP.Scanning.Core.Pipeline.Collection.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Pipeline.Contracts.Page;

namespace PnP.Scanning.Core.Pipeline.Collection.Page;

internal sealed class PublishingLayoutSourceReader
{
    private readonly ConcurrentDictionary<(Guid Site, string Path), string> observed = new();
    private readonly ConcurrentDictionary<(Guid Site, string Path), Lazy<Task<ClassicPublishingLayoutSource>>> reads = new();

    internal void Seed(Guid site, string? path, string? status)
    {
        if (!string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(status))
            observed.TryAdd((site, path.ToUpperInvariant()), status);
    }

    internal Task<ClassicPublishingLayoutSource> ReadAsync(Guid site, Uri siteUrl, string url,
        Func<string, Task<string>> readStatus, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!PublishingLayoutStatusReader.TryResolveServerRelativeUrl(siteUrl, url, out var path))
            return Task.FromResult(new ClassicPublishingLayoutSource(url, null, null,
                new SourceReadState("Failed", "Publishing layout URL is outside the selected site collection.")));
        return reads.GetOrAdd((site, path.ToUpperInvariant()), key => new Lazy<Task<ClassicPublishingLayoutSource>>(async () =>
        {
            string? raw = null;
            try
            {
                token.ThrowIfCancellationRequested();
                raw = observed.TryGetValue(key, out var value) ? value : await readStatus(path).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return new(path, raw, FileCustomizationStatus.Normalize(raw), SourceReadState.Complete);
            }
            catch (Exception error) when (error is not OperationCanceledException && !token.IsCancellationRequested)
            { return new(path, raw, null, ClassicPageOnlineSource.Failure(error)); }
        })).Value;
    }
}
