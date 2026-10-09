#nullable enable
using PnP.Scanning.Core.Pipeline.Contracts;

namespace PnP.Scanning.Core.Pipeline.Analysis;

internal sealed class ClassicPageInputIndex
{
    internal ClassicPageScopeSource Scope { get; private set; } = null!;
    internal Dictionary<string, ClassicPageWebSource> Webs { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, ClassicPageDiscoveryRow> Pages { get; } = new(StringComparer.Ordinal);
    internal List<ClassicPageDiscoveryRow> Discovery { get; } = [];
    internal Dictionary<int, ClassicPageAuditChunkSource> AuditChunks { get; } = [];

    // Keep identities and shared metadata, not all page XML / audit bodies, in the index.
    internal static async Task<ClassicPageInputIndex> CreateAsync(IAsyncEnumerable<SourceRecord> records, CancellationToken token)
    {
        var index = new ClassicPageInputIndex();
        var pageReferences = new Dictionary<string, Guid?>();
        var discoveryScopes = new HashSet<string>();
        var auditReferences = new Dictionary<(int Chunk, int Page), (string Query, string? Next)>();
        await foreach (var source in records.WithCancellation(token))
        {
            switch (ClassicPageSourceJson.Kind(source))
            {
                case "Scope":
                    if (index.Scope != null) throw new SnapshotIntegrityException("Classic Pages requires one fixed assessment scope.");
                    index.Scope = ClassicPageSourceJson.Read<ClassicPageScopeSource>(source.Artifact.GetBytes());
                    break;
                case "Web":
                    var web = ClassicPageSourceJson.Read<ClassicPageWebSource>(source.Artifact.GetBytes());
                    if (!index.Webs.TryAdd(ClassicPageSourceJson.WebKey(web.SiteUrl, web.WebUrl), web))
                        throw new SnapshotIntegrityException("Duplicate Web source identity.");
                    break;
                case "Discovery":
                    var discovery = ClassicPageSourceJson.Read<ClassicPageDiscoverySource>(source.Artifact.GetBytes());
                    var webKey = ClassicPageSourceJson.WebKey(discovery.SiteUrl, discovery.WebUrl);
                    if (!discoveryScopes.Add(webKey)) throw new SnapshotIntegrityException("Duplicate Web discovery input.");
                    foreach (var row in discovery.Rows)
                    {
                        if (!string.Equals(row.SiteUrl, discovery.SiteUrl, StringComparison.OrdinalIgnoreCase) || row.WebUrl != discovery.WebUrl)
                            throw new SnapshotIntegrityException("Discovery row scope differs from its owner.");
                        index.Discovery.Add(row);
                        if (row.RowType == "Page" && !index.Pages.TryAdd(webKey + "|" + row.RecordKey, row))
                            throw new SnapshotIntegrityException("Duplicate physical Page identity.");
                    }
                    break;
                case "Page":
                    var page = ClassicPageSourceJson.Read<ClassicPageItemSource>(source.Artifact.GetBytes());
                    if (!pageReferences.TryAdd(ClassicPageSourceJson.WebKey(page.SiteUrl, page.WebUrl) + "|" + page.DiscoveryKey, page.ListId))
                        throw new SnapshotIntegrityException("Duplicate Page acquisition identity.");
                    break;
                case "AuditChunk":
                    var chunk = ClassicPageSourceJson.Read<ClassicPageAuditChunkSource>(source.Artifact.GetBytes());
                    if (!index.AuditChunks.TryAdd(chunk.Chunk, chunk)) throw new SnapshotIntegrityException("Duplicate audit chunk.");
                    break;
                case "AuditPage":
                    var audit = ClassicPageSourceJson.Read<ClassicPageAuditPageSource>(source.Artifact.GetBytes());
                    if (!auditReferences.TryAdd((audit.Chunk, audit.Page), (audit.QueryId, audit.NextLink)))
                        throw new SnapshotIntegrityException("Duplicate audit record page.");
                    break;
            }
        }
        if (index.Scope == null || index.Scope.AuditWindowEnd < index.Scope.AuditWindowStart ||
            index.Scope.Sites.Distinct(StringComparer.OrdinalIgnoreCase).Count() != index.Scope.Sites.Length ||
            !index.Scope.Sites.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(index.Scope.SiteScopes.Select(x => x.SiteUrl)))
            throw new SnapshotIntegrityException("Classic Page scope or audit window is invalid.");
        if (index.Scope.SiteScopes.Any(x => x.WebUrls.Length != x.Templates.Length || x.State.Status is "Pending" or "Running"))
            throw new SnapshotIntegrityException("Web enumeration has an unsettled or invalid checkpoint.");
        var expected = index.Scope.SiteScopes.SelectMany(s => s.WebUrls.Select(w => ClassicPageSourceJson.WebKey(s.SiteUrl, w))).ToHashSet();
        if (!expected.SetEquals(index.Webs.Keys)) throw new SnapshotIntegrityException("Web inputs do not match the fixed enumeration.");
        foreach (var key in discoveryScopes)
            if (!index.Webs.TryGetValue(key, out var web) || web.WebId == Guid.Empty || web.SiteId == Guid.Empty)
                throw new SnapshotIntegrityException("Discovery references an unavailable or foreign Web source.");
        if (index.Webs.Any(x => x.Value.State.Succeeded && !discoveryScopes.Contains(x.Key)))
            throw new SnapshotIntegrityException("A collected Web has no discovery result.");
        if (!pageReferences.Keys.ToHashSet().SetEquals(index.Pages.Keys) ||
            pageReferences.Any(x => index.Pages[x.Key].ListId != x.Value))
            throw new SnapshotIntegrityException("A Page input references a foreign identity, or a discovered Page has no acquisition record.");
        foreach (var page in auditReferences)
            if (!index.AuditChunks.TryGetValue(page.Key.Chunk, out var chunk) || chunk.QueryId != page.Value.Query)
                throw new SnapshotIntegrityException("Audit page references a foreign or unsettled query.");
        if (!index.Scope.Options.SkipUsageInformation)
        {
            var cursor = index.Scope.AuditWindowStart;
            foreach (var chunk in index.AuditChunks.Values.OrderBy(x => x.Start))
            {
                if (chunk.Start != cursor || chunk.End <= chunk.Start || chunk.End > index.Scope.AuditWindowEnd ||
                    chunk.Status is not ("succeeded" or "failed" or "skipped"))
                    throw new SnapshotIntegrityException("Audit chunks do not cover the pinned window exactly.");
                cursor = chunk.End;
                if (chunk.Status == "succeeded")
                {
                    var pages = auditReferences.Where(x => x.Key.Chunk == chunk.Chunk).OrderBy(x => x.Key.Page).ToArray();
                    if (pages.Length == 0 || pages.Where((x, i) => x.Key.Page != i).Any() || pages[^1].Value.Next != null ||
                        pages.Take(pages.Length - 1).Any(x => x.Value.Next == null))
                        throw new SnapshotIntegrityException("Succeeded audit chunk has incomplete pagination receipts.");
                }
            }
            if (cursor != index.Scope.AuditWindowEnd) throw new SnapshotIntegrityException("The audit window has an unrecorded acquisition gap.");
        }
        return index;
    }
}
