#nullable enable
using PnP.Scanning.Core.Pipeline.Contracts.Audit;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Page;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Pipeline.Contracts.Web;
using PnP.Scanning.Core.Pipeline.Contracts.List;
using PnP.Scanning.Core.Pipeline.Analysis.Page;

namespace PnP.Scanning.Core.Pipeline.Analysis.Module;

/// <summary>
/// Indexes fixed source scope and native identities while validating acquisition completeness.
/// </summary>
internal sealed class ClassicPageInputIndex
{
    internal ClassicPageScopeSource Scope { get; private set; } = null!;
    internal Dictionary<string, ClassicPageWebSource> Webs { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, ClassicPageDiscoveryRow> Pages { get; } = new(StringComparer.Ordinal);
    internal List<ClassicPageDiscoveryRow> Discovery { get; } = [];
    internal Dictionary<(Guid Site, Guid File), SPFileSource> Files { get; } = [];
    internal bool HasPhysicalFileContract { get; private set; }
    internal Dictionary<string, ClassicBlogListScopeSource> BlogLists { get; } = new(StringComparer.Ordinal);
    internal List<ClassicPageBlogBatchSource> BlogBatches { get; } = [];
    internal bool HasContentItemContract { get; private set; }
    internal bool ContentItemsComplete { get; private set; }
    internal Dictionary<int, ClassicPageAuditChunkSource> AuditChunks { get; } = [];
    internal Dictionary<string, ClassicComponentSource> Components { get; } = [];

    // Keep identities and shared metadata, not all page XML / audit bodies, in the index.
    /// <summary>
    /// Indexes streamed source records and rejects inconsistent ownership, scope, or pagination coverage.
    /// </summary>
    internal static async Task<ClassicPageInputIndex> CreateAsync(IAsyncEnumerable<SourceRecord> records, CancellationToken token,
        Guid? assessmentId = null)
    {
        ClassicPageInputIndex? index = new ClassicPageInputIndex();
        Dictionary<string, Guid?>? pageReferences = new Dictionary<string, Guid?>();
        Dictionary<string, SPFileSource>? pageFacts = new Dictionary<string, SPFileSource>();
        List<(string Web, SPFileSource File)>? fileOwners = new List<(string Web, SPFileSource File)>();
        HashSet<string>? discoveryScopes = new HashSet<string>();
        HashSet<string>? physicalFileScopes = new HashSet<string>();
        Dictionary<(int Chunk, int Page), (string Query, string? Next)>? auditReferences = new Dictionary<(int Chunk, int Page), (string Query, string? Next)>();
        await foreach (SourceRecord? source in records.WithCancellation(token))
        {
            switch (ClassicPageSourceJson.Kind(source))
            {
                case "ClassicComponents":
                    ClassicComponentSource? components = ClassicPageSourceJson.Read<ClassicComponentSource>(source.Artifact.GetBytes());
                    if (!index.Components.TryAdd(ClassicPageSourceJson.WebKey(components.SiteUrl, components.WebUrl), components))
                        throw new SnapshotIntegrityException("Duplicate Classic component scope.");
                    break;
                case "Scope":
                    if (index.Scope != null) throw new SnapshotIntegrityException("Classic Pages requires one fixed assessment scope.");
                    index.Scope = ClassicPageSourceJson.Read<ClassicPageScopeSource>(source.Artifact.GetBytes());
                    break;
                case "Web":
                    ClassicPageWebSource? web = ClassicPageSourceJson.Read<ClassicPageWebSource>(source.Artifact.GetBytes());
                    if (!index.Webs.TryAdd(ClassicPageSourceJson.WebKey(web.SiteUrl, web.WebUrl), web))
                        throw new SnapshotIntegrityException("Duplicate Web source identity.");
                    break;
                case "Discovery":
                    ClassicPageDiscoverySource? discovery = ClassicPageSourceJson.Read<ClassicPageDiscoverySource>(source.Artifact.GetBytes());
                    string? webKey = ClassicPageSourceJson.WebKey(discovery.SiteUrl, discovery.WebUrl);
                    if (!discoveryScopes.Add(webKey)) throw new SnapshotIntegrityException("Duplicate Web discovery input.");
                    if (discovery.Files != null) physicalFileScopes.Add(webKey);
                    foreach (SPFileSource? file in discovery.Files ?? [])
                    {
                        if (file.ScanId == Guid.Empty || file.SiteUrl != discovery.SiteUrl || file.WebUrl != discovery.WebUrl)
                            throw new SnapshotIntegrityException("Physical file scope differs from its discovery owner.");
                        (Guid SiteCollectionId, Guid FileUniqueId) key = (file.SiteCollectionId, file.FileUniqueId);
                        fileOwners.Add((webKey, file));
                        index.Files[key] = SPFileFactConsolidator.Consolidate(index.Files.GetValueOrDefault(key), file);
                    }
                    foreach (ClassicPageDiscoveryRow? row in discovery.Rows)
                    {
                        if (!string.Equals(row.SiteUrl, discovery.SiteUrl, StringComparison.OrdinalIgnoreCase) || row.WebUrl != discovery.WebUrl)
                            throw new SnapshotIntegrityException("Discovery row scope differs from its owner.");
                        index.Discovery.Add(row);
                        if (row.RowType == "Page" && !index.Pages.TryAdd(webKey + "|" + row.RecordKey, row))
                            throw new SnapshotIntegrityException("Duplicate physical Page identity.");
                    }
                    break;
                case "Page":
                    ClassicPageItemSource? page = ClassicPageSourceJson.Read<ClassicPageItemSource>(source.Artifact.GetBytes());
                    if (!pageReferences.TryAdd(ClassicPageSourceJson.WebKey(page.SiteUrl, page.WebUrl) + "|" + page.DiscoveryKey, page.ListId))
                        throw new SnapshotIntegrityException("Duplicate Page acquisition identity.");
                    if (page.FileFacts != null)
                    {
                        SPFileSource? file = page.FileFacts;
                        pageFacts.Add(ClassicPageSourceJson.WebKey(page.SiteUrl, page.WebUrl) + "|" + page.DiscoveryKey, file);
                        (Guid SiteCollectionId, Guid FileUniqueId) key = (file.SiteCollectionId, file.FileUniqueId);
                        index.Files[key] = SPFileFactConsolidator.Consolidate(index.Files.GetValueOrDefault(key), file);
                    }
                    break;
                case "BlogLists":
                    ClassicBlogListScopeSource? blogScope = ClassicPageSourceJson.Read<ClassicBlogListScopeSource>(source.Artifact.GetBytes());
                    if (!index.BlogLists.TryAdd(ClassicPageSourceJson.WebKey(blogScope.SiteUrl, blogScope.WebUrl), blogScope))
                        throw new SnapshotIntegrityException("Duplicate Blog/Delve list-scope input.");
                    if (blogScope.Lists.Any(x => x.ListId == Guid.Empty || string.IsNullOrWhiteSpace(x.ListUrl) ||
                        x.PageType is not ("BlogPage" or "DelveBlogPage" or "ASPXPage")) ||
                        blogScope.Lists.Select(x => x.ListId).Distinct().Count() != blogScope.Lists.Length)
                        throw new SnapshotIntegrityException("Blog/Delve list scope has invalid or duplicate native identities.");
                    break;
                case "Blog":
                    index.BlogBatches.Add(ClassicPageSourceJson.Read<ClassicPageBlogBatchSource>(source.Artifact.GetBytes()));
                    break;
                case "AuditChunk":
                    ClassicPageAuditChunkSource? chunk = ClassicPageSourceJson.Read<ClassicPageAuditChunkSource>(source.Artifact.GetBytes());
                    if (!index.AuditChunks.TryAdd(chunk.Chunk, chunk)) throw new SnapshotIntegrityException("Duplicate audit chunk.");
                    break;
                case "AuditPage":
                    ClassicPageAuditPageSource? audit = ClassicPageSourceJson.Read<ClassicPageAuditPageSource>(source.Artifact.GetBytes());
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
        HashSet<string>? expected = index.Scope.SiteScopes.SelectMany(s => s.WebUrls.Select(w => ClassicPageSourceJson.WebKey(s.SiteUrl, w))).ToHashSet();
        if (!expected.SetEquals(index.Webs.Keys)) throw new SnapshotIntegrityException("Web inputs do not match the fixed enumeration.");
        foreach (string? key in discoveryScopes)
            if (!index.Webs.TryGetValue(key, out ClassicPageWebSource? web) || web.WebId == Guid.Empty || web.SiteId == Guid.Empty)
                throw new SnapshotIntegrityException("Discovery references an unavailable or foreign Web source.");
        if (index.Webs.Any(x => x.Value.State.Succeeded && !discoveryScopes.Contains(x.Key)))
            throw new SnapshotIntegrityException("A collected Web has no discovery result.");
        if (!pageReferences.Keys.ToHashSet().SetEquals(index.Pages.Keys) ||
            pageReferences.Any(x => index.Pages[x.Key].ListId != x.Value))
            throw new SnapshotIntegrityException("A Page input references a foreign identity, or a discovered Page has no acquisition record.");
        foreach ((string? key, SPFileSource? file) in pageFacts)
        {
            ClassicPageDiscoveryRow? page = index.Pages[key];
            if (file.ScanId != page.ScanId || file.SiteCollectionId != page.SiteCollectionId || file.FileUniqueId != page.FileUniqueId)
                throw new SnapshotIntegrityException("Page metadata facts refer to a different physical file.");
        }
        foreach ((string? owner, SPFileSource? file) in fileOwners)
        {
            ClassicPageWebSource? web = index.Webs[owner];
            if (file.SiteCollectionId != web.SiteId || file.WebId.HasValue && file.WebId != web.WebId)
                throw new SnapshotIntegrityException("Physical file identity differs from its owning Web source.");
        }
        foreach (KeyValuePair<(int Chunk, int Page), (string Query, string? Next)> page in auditReferences)
            if (!index.AuditChunks.TryGetValue(page.Key.Chunk, out ClassicPageAuditChunkSource? chunk) || chunk.QueryId != page.Value.Query)
                throw new SnapshotIntegrityException("Audit page references a foreign or unsettled query.");
        if (!index.Scope.Options.SkipUsageInformation)
        {
            DateTime cursor = index.Scope.AuditWindowStart;
            foreach (ClassicPageAuditChunkSource? chunk in index.AuditChunks.Values.OrderBy(x => x.Start))
            {
                if (chunk.Start != cursor || chunk.End <= chunk.Start || chunk.End > index.Scope.AuditWindowEnd ||
                    chunk.Status is not ("succeeded" or "failed" or "skipped"))
                    throw new SnapshotIntegrityException("Audit chunks do not cover the pinned window exactly.");
                cursor = chunk.End;
                if (chunk.Status == "succeeded")
                {
                    KeyValuePair<(int Chunk, int Page), (string Query, string? Next)>[]? pages = auditReferences.Where(x => x.Key.Chunk == chunk.Chunk).OrderBy(x => x.Key.Page).ToArray();
                    if (pages.Length == 0 || pages.Where((x, i) => x.Key.Page != i).Any() || pages[^1].Value.Next != null ||
                        pages.Take(pages.Length - 1).Any(x => x.Value.Next == null))
                        throw new SnapshotIntegrityException("Succeeded audit chunk has incomplete pagination receipts.");
                }
            }
            if (cursor != index.Scope.AuditWindowEnd) throw new SnapshotIntegrityException("The audit window has an unrecorded acquisition gap.");
        }
        index.HasPhysicalFileContract = discoveryScopes.Count > 0 && physicalFileScopes.SetEquals(discoveryScopes);
        foreach ((string? owner, ClassicBlogListScopeSource? scope) in index.BlogLists)
            if (!index.Webs.ContainsKey(owner))
                throw new SnapshotIntegrityException("Blog/Delve scope refers to a foreign Web.");
        HashSet<(Guid Site, Guid List, int Item)>? contentItems = new HashSet<(Guid Site, Guid List, int Item)>();
        foreach (ClassicPageBlogBatchSource? batch in index.BlogBatches)
        {
            string? owner = ClassicPageSourceJson.WebKey(batch.SiteUrl, batch.WebUrl);
            if (!index.Webs.TryGetValue(owner, out ClassicPageWebSource? web) || batch.ListId == Guid.Empty ||
                batch.PageType is not ("BlogPage" or "DelveBlogPage" or "ASPXPage") || (!batch.State.Succeeded && batch.NextPage != null))
                throw new SnapshotIntegrityException("Blog/Delve batch has a foreign owner or unsettled paging state.");
            if (index.BlogLists.TryGetValue(owner, out ClassicBlogListScopeSource? scope))
            {
                ClassicBlogListSource? list = scope.Lists.SingleOrDefault(x => x.ListId == batch.ListId);
                if (list == null || list.ListUrl != batch.ListUrl || list.PageType != batch.PageType ||
                    list.DisplayFormUrl != batch.DisplayFormUrl ||
                    !(list.ContentTypeForms ?? []).SequenceEqual(batch.ContentTypeForms ?? []))
                    throw new SnapshotIntegrityException("Blog/Delve batch differs from its fixed list scope.");
            }
            foreach (Dictionary<string, SourceField>? fields in batch.Items)
                if (!int.TryParse(fields.GetValueOrDefault("ID")?.Text, out int item) || item <= 0 ||
                    !contentItems.Add((web.SiteId, batch.ListId, item)))
                    throw new SnapshotIntegrityException("Blog/Delve item identity is absent or repeated across pages.");
        }
        index.HasContentItemContract = expected.Count > 0 && expected.SetEquals(index.BlogLists.Keys);
        index.ContentItemsComplete = index.HasContentItemContract && index.BlogLists.Values.All(x => x.State.Succeeded);
        foreach ((string? owner, ClassicBlogListScopeSource? scope) in index.BlogLists)
        {
            foreach (ClassicBlogListSource? list in scope.Lists)
            {
                ClassicPageBlogBatchSource[]? batches = index.BlogBatches.Where(x => ClassicPageSourceJson.WebKey(x.SiteUrl, x.WebUrl) == owner &&
                    x.ListId == list.ListId).OrderBy(x => x.PageOrdinal).ToArray();
                if (batches.Length == 0 || batches.Where((x, i) => x.PageOrdinal != i).Any() ||
                    batches[0].PagingToken != null || batches[^1].NextPage != null ||
                    batches.Take(batches.Length - 1).Any(x => x.NextPage == null) ||
                    batches.Skip(1).Where((x, i) => x.PagingToken != batches[i].NextPage).Any())
                    throw new SnapshotIntegrityException("Blog/Delve list acquisition has an incomplete pagination chain.");
                if (batches.Any(x => !x.State.Succeeded)) index.ContentItemsComplete = false;
            }
        }
        if (assessmentId is Guid expectedAssessment &&
            (index.Files.Values.Any(file => file.ScanId != expectedAssessment) ||
             index.Discovery.Concat(index.Scope.Evidence).Any(row => row.ScanId != expectedAssessment)))
            throw new SnapshotIntegrityException("Classic Page evidence belongs to a different assessment.");
        if (index.Components.Keys.Any(key => !index.Webs.ContainsKey(key)))
            throw new SnapshotIntegrityException("Classic component evidence has a foreign Web.");
        if ((index.Components.Count > 0 || index.Scope.ClassicComponentsRequested) && !expected.SetEquals(index.Components.Keys))
            throw new SnapshotIntegrityException("Classic component evidence does not cover the fixed Web scope.");
        foreach (ClassicComponentSource? component in index.Components.Values)
        {
            if (component.Rows.Keys.Any(kind => kind is not ("ClassicList" or "ClassicInfoPath" or "Workflow" or
                "ClassicExtensibility" or "ClassicUserCustomAction" or "ClassicWebSummary")))
                throw new SnapshotIntegrityException("Classic component evidence has an unsupported row contract.");
            foreach (System.Text.Json.JsonElement row in component.Rows.Values.SelectMany(rows => rows))
                if (!row.TryGetProperty("ScanId", out System.Text.Json.JsonElement scan) || scan.GetGuid() != assessmentId ||
                    !row.TryGetProperty("SiteUrl", out System.Text.Json.JsonElement site) || site.GetString() != component.SiteUrl ||
                    !row.TryGetProperty("WebUrl", out System.Text.Json.JsonElement web) || web.GetString() != component.WebUrl)
                    throw new SnapshotIntegrityException("Classic component evidence has foreign ownership.");
        }
        return index;
    }
}
