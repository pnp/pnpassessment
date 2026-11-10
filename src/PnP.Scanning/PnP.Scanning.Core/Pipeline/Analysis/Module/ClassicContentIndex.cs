#nullable enable
using PnP.Scanning.Core.Pipeline.Analysis.Page;
using PnP.Scanning.Core.Pipeline.Contracts.Audit;
using PnP.Scanning.Core.Pipeline.Contracts.List;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Page;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Pipeline.Contracts.Web;

namespace PnP.Scanning.Core.Pipeline.Analysis.Module;

/// <summary>
/// Indexes acquired objects and evidence by native identity, rejecting conflicting ownership without conflating them with Pages.
/// </summary>
internal sealed class ClassicContentIndex
{
    internal ClassicCollectionScope Scope { get; private set; } = null!;
    internal Dictionary<string, WebFacts> Webs { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, DiscoveryEvidence> FileCandidates { get; } = new(StringComparer.Ordinal);
    internal List<DiscoveryEvidence> Evidence { get; } = [];
    internal Dictionary<(Guid Site, Guid File), PhysicalFileFacts> Files { get; } = [];
    internal Dictionary<string, WebListItemQueries> ItemQueries { get; } = new(StringComparer.Ordinal);
    internal List<ListItemBatch> ItemBatches { get; } = [];
    internal Dictionary<(Guid Site, Guid List, int Item), SelectedItem> Items { get; } = [];
    internal Dictionary<int, ClassicPageAuditChunkSource> AuditChunks { get; } = [];
    internal Dictionary<string, ClassicComponentSource> Components { get; } = [];

    /// <summary>
    /// Associates one returned item with its captured query metadata, not a preselected page family.
    /// </summary>
    internal sealed record SelectedItem(ListItemBatch Batch, ListItemFacts Item);

    /// <summary>
    /// Validates fixed scopes, receipts, native file-item relationships, and pagination before any unified Page projection.
    /// </summary>
    internal static async Task<ClassicContentIndex> CreateAsync(
        IAsyncEnumerable<SourceRecord> records,
        CancellationToken token,
        Guid? assessmentId = null)
    {
        ClassicContentIndex index = new();
        Dictionary<string, FilePageEvidence> enrichment = new(StringComparer.Ordinal);
        HashSet<string> inventories = new(StringComparer.Ordinal);
        Dictionary<(Guid Site, Guid File), (Guid List, int Item)> nativeLinks = [];
        Dictionary<(int Chunk, int Page), (string Query, string? Next)> auditPages = [];
        await foreach (SourceRecord source in records.WithCancellation(token))
        {
            switch (CollectionJson.Kind(source))
            {
                case "CollectionScope":
                    if (index.Scope != null)
                        throw new SnapshotIntegrityException("Collection requires one fixed scope.");
                    index.Scope = CollectionJson.Read<ClassicCollectionScope>(source.Artifact.GetBytes());
                    break;
                case "WebFacts":
                    WebFacts web = CollectionJson.Read<WebFacts>(source.Artifact.GetBytes());
                    if (!index.Webs.TryAdd(CollectionJson.WebKey(web.SiteUrl, web.WebUrl), web))
                        throw new SnapshotIntegrityException("Duplicate Web facts.");
                    break;
                case "FileInventory":
                    WebFileInventory inventory = CollectionJson.Read<WebFileInventory>(source.Artifact.GetBytes());
                    string owner = CollectionJson.WebKey(inventory.SiteUrl, inventory.WebUrl);
                    if (!inventories.Add(owner)) throw new SnapshotIntegrityException("Duplicate Web file inventory.");
                    foreach (PhysicalFileFacts file in inventory.Files ?? [])
                        index.RegisterFile(file, owner, nativeLinks, assessmentId);
                    foreach (DiscoveryEvidence evidence in inventory.Evidence)
                    {
                        if (!string.Equals(evidence.SiteUrl, inventory.SiteUrl, StringComparison.OrdinalIgnoreCase) ||
                            evidence.WebUrl != inventory.WebUrl)
                            throw new SnapshotIntegrityException(
                                "Discovery evidence belongs to a different Web inventory.");
                        index.Evidence.Add(evidence);
                        if (evidence.RowType == "Page" && !index.FileCandidates.TryAdd(
                                owner + "|" + evidence.RecordKey,
                                evidence))
                            throw new SnapshotIntegrityException("Repeated file-enrichment candidate identity.");
                    }

                    break;
                case "FileEnrichment":
                    FilePageEvidence fileEvidence = CollectionJson.Read<FilePageEvidence>(source.Artifact.GetBytes());
                    string fileEvidenceOwner = CollectionJson.WebKey(fileEvidence.SiteUrl, fileEvidence.WebUrl);
                    if (!enrichment.TryAdd(fileEvidenceOwner + "|" + fileEvidence.DiscoveryKey, fileEvidence))
                        throw new SnapshotIntegrityException("Repeated file-enrichment acquisition.");
                    if (fileEvidence.FileFacts != null)
                        index.RegisterFile(fileEvidence.FileFacts, fileEvidenceOwner, nativeLinks, assessmentId);
                    break;
                case "ListItemQueries":
                    WebListItemQueries queries = CollectionJson.Read<WebListItemQueries>(source.Artifact.GetBytes());
                    if (!index.ItemQueries.TryAdd(CollectionJson.WebKey(queries.SiteUrl, queries.WebUrl), queries) ||
                        queries.Queries.Select(query => query.ListId).Distinct().Count() != queries.Queries.Length ||
                        queries.Queries.Any(query =>
                            query.ListId == Guid.Empty || string.IsNullOrWhiteSpace(query.ListUrl) ||
                            !Enum.IsDefined(query.QueryKind) || query.ListTemplate <= 0))
                        throw new SnapshotIntegrityException("Invalid or repeated native list-query scope.");
                    break;
                case "ListItems":
                    index.ItemBatches.Add(CollectionJson.Read<ListItemBatch>(source.Artifact.GetBytes()));
                    break;
                case "ClassicComponents":
                    ClassicComponentSource components =
                        CollectionJson.Read<ClassicComponentSource>(source.Artifact.GetBytes());
                    if (!index.Components.TryAdd(
                            CollectionJson.WebKey(components.SiteUrl, components.WebUrl),
                            components))
                        throw new SnapshotIntegrityException("Duplicate Classic component scope.");
                    break;
                case "AuditChunk":
                    ClassicPageAuditChunkSource chunk =
                        CollectionJson.Read<ClassicPageAuditChunkSource>(source.Artifact.GetBytes());
                    if (!index.AuditChunks.TryAdd(chunk.Chunk, chunk))
                        throw new SnapshotIntegrityException("Duplicate audit chunk.");
                    break;
                case "AuditPage":
                    ClassicPageAuditPageSource audit =
                        CollectionJson.Read<ClassicPageAuditPageSource>(source.Artifact.GetBytes());
                    if (!auditPages.TryAdd((audit.Chunk, audit.Page), (audit.QueryId, audit.NextLink)))
                        throw new SnapshotIntegrityException("Duplicate audit response page.");
                    break;
            }
        }

        if (index.Scope == null || index.Scope.AuditWindowEnd < index.Scope.AuditWindowStart ||
            index.Scope.SiteCollectionUrls.Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            index.Scope.SiteCollectionUrls.Length ||
            index.Scope.SiteCollections.Length != index.Scope.SiteCollectionUrls.Length ||
            !index.Scope.SiteCollectionUrls.ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(index.Scope.SiteCollections.Select(site => site.SiteUrl)))
            throw new SnapshotIntegrityException("The fixed site-collection scope or audit window is invalid.");
        HashSet<string> expectedWebs = new(StringComparer.Ordinal);
        foreach (Contracts.Site.SiteCollectionScope site in index.Scope.SiteCollections)
        {
            if (site.State.Status is "Pending" or "Running")
                throw new SnapshotIntegrityException("Web enumeration has not settled.");
            foreach (WebScope webScope in site.Webs)
                if (!string.Equals(webScope.SiteCollectionUrl, site.SiteUrl, StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(webScope.WebTemplate) ||
                    !expectedWebs.Add(CollectionJson.WebKey(webScope.SiteCollectionUrl, webScope.WebUrl)) ||
                    !index.Webs.TryGetValue(
                        CollectionJson.WebKey(webScope.SiteCollectionUrl, webScope.WebUrl),
                        out WebFacts? facts) || facts.WebTemplate != webScope.WebTemplate)
                    throw new SnapshotIntegrityException(
                        "Web scopes have foreign owners, missing templates, or repeated identities.");
        }

        if (!expectedWebs.SetEquals(index.Webs.Keys))
            throw new SnapshotIntegrityException("Web facts differ from their fixed scope.");
        if (!expectedWebs.SetEquals(index.ItemQueries.Keys))
            throw new SnapshotIntegrityException("Selected-item query scope is incomplete.");
        foreach (string owner in inventories)
            if (!index.Webs.TryGetValue(owner, out WebFacts? web) || web.SiteCollectionId == Guid.Empty ||
                web.WebId == Guid.Empty)
                throw new SnapshotIntegrityException("File inventory references an unobserved Web identity.");
        if (index.Webs.Any(web => web.Value.State.Succeeded && !inventories.Contains(web.Key)) ||
            !enrichment.Keys.ToHashSet().SetEquals(index.FileCandidates.Keys))
            throw new SnapshotIntegrityException("File inventory or its required enrichment receipts are missing.");
        foreach (KeyValuePair<string, FilePageEvidence> source in enrichment)
        {
            DiscoveryEvidence candidate = index.FileCandidates[source.Key];
            if (candidate.ListId != source.Value.ListId || source.Value.FileFacts is PhysicalFileFacts file &&
                (file.SiteCollectionId != candidate.SiteCollectionId || file.FileUniqueId != candidate.FileUniqueId ||
                 file.ScanId != candidate.ScanId))
                throw new SnapshotIntegrityException(
                    "File enrichment points to different native file or item ownership.");
        }

        foreach (PhysicalFileFacts file in index.Files.Values)
        {
            string owner = CollectionJson.WebKey(file.SiteUrl!, file.WebUrl!);
            if (!index.Webs.TryGetValue(owner, out WebFacts? web) || web.SiteCollectionId != file.SiteCollectionId ||
                file.WebId.HasValue && file.WebId != web.WebId)
                throw new SnapshotIntegrityException("Physical file identity differs from its owning Web.");
        }

        index.ValidateItems(nativeLinks, assessmentId);
        index.ValidateAudit(auditPages);
        if (assessmentId is Guid expectedAssessment && index.Evidence.Concat(index.Scope.Evidence)
                .Concat(index.Scope.SiteCollections.SelectMany(site => site.Evidence ?? []))
                .Any(row => row.ScanId != expectedAssessment))
            throw new SnapshotIntegrityException("Discovery evidence belongs to a different assessment.");
        if (index.Components.Keys.Any(owner => !expectedWebs.Contains(owner)) ||
            index.Scope.ClassicComponentsRequested && !expectedWebs.SetEquals(index.Components.Keys))
            throw new SnapshotIntegrityException("Classic component evidence has incomplete or foreign Web scope.");
        foreach (ClassicComponentSource component in index.Components.Values)
        {
            if (component.Rows.Keys.Any(kind =>
                    kind is not ("ClassicList" or "ClassicInfoPath" or "Workflow" or "ClassicExtensibility"
                        or "ClassicUserCustomAction" or "ClassicWebSummary")))
                throw new SnapshotIntegrityException("Unsupported Classic component contract.");
            foreach (System.Text.Json.JsonElement row in component.Rows.Values.SelectMany(values => values))
                if (row.GetProperty("ScanId").GetGuid() != assessmentId ||
                    row.GetProperty("SiteUrl").GetString() != component.SiteUrl ||
                    row.GetProperty("WebUrl").GetString() != component.WebUrl)
                    throw new SnapshotIntegrityException("Classic component evidence has foreign ownership.");
        }

        return index;
    }

    /// <summary>
    /// Consolidates observations by native file key while rejecting contradictory native backing-item relationships.
    /// </summary>
    private void RegisterFile(
        PhysicalFileFacts file,
        string owner,
        Dictionary<(Guid Site, Guid File), (Guid List, int Item)> links,
        Guid? assessmentId)
    {
        if (file.ScanId == Guid.Empty || assessmentId.HasValue && file.ScanId != assessmentId || file.SiteUrl != null &&
            file.WebUrl != null && CollectionJson.WebKey(file.SiteUrl, file.WebUrl) != owner)
            throw new SnapshotIntegrityException("Physical file evidence belongs to a different assessment or Web.");
        if (file.SiteCollectionId == Guid.Empty || file.FileUniqueId == Guid.Empty || file.ListId == Guid.Empty ||
            file.ListItemId <= 0)
            throw new SnapshotIntegrityException("Physical file facts contain an empty or invalid native identity.");
        (Guid Site, Guid File) key = (file.SiteCollectionId, file.FileUniqueId);
        if (file.ListId is Guid list && file.ListItemId is int item)
        {
            (Guid List, int Item) association = (list, item);
            if (list == Guid.Empty || item <= 0 ||
                links.TryGetValue(key, out (Guid List, int Item) previous) && previous != association)
                throw new SnapshotIntegrityException("A physical file has conflicting native backing-item identities.");
            links[key] = association;
        }

        Files[key] = SPFileFactConsolidator.Consolidate(Files.GetValueOrDefault(key), file);
    }

    /// <summary>
    /// Validates item acquisition scope and pagination, merging only identical repeated native objects and verified file associations.
    /// </summary>
    private void ValidateItems(Dictionary<(Guid Site, Guid File), (Guid List, int Item)> links, Guid? assessmentId)
    {
        foreach (KeyValuePair<string, WebListItemQueries> queries in ItemQueries)
            if (!Webs.ContainsKey(queries.Key))
                throw new SnapshotIntegrityException("List-item queries have a foreign Web owner.");
        foreach (ListItemBatch batch in ItemBatches)
        {
            string owner = CollectionJson.WebKey(batch.SiteUrl, batch.WebUrl);
            if (!Webs.TryGetValue(owner, out WebFacts? web) ||
                !ItemQueries.TryGetValue(owner, out WebListItemQueries? queries) || batch.ListId == Guid.Empty ||
                !Enum.IsDefined(batch.QueryKind) || !batch.State.Succeeded && batch.NextPage != null)
                throw new SnapshotIntegrityException("List-item batch has foreign scope or unsettled paging.");
            ListItemQueryScope? query = queries.Queries.SingleOrDefault(value => value.ListId == batch.ListId);
            if (query == null || query.QueryKind != batch.QueryKind || query.ListTemplate != batch.ListTemplate ||
                query.ListUrl != batch.ListUrl || query.DisplayFormUrl != batch.DisplayFormUrl ||
                !(query.ContentTypeForms ?? []).SequenceEqual(batch.ContentTypeForms ?? []))
                throw new SnapshotIntegrityException(
                    "List-item batch differs from its fixed query and observed list template.");
            foreach (ListItemFacts item in batch.Items)
            {
                if (web.SiteCollectionId == Guid.Empty || web.WebId == Guid.Empty || item.ListItemId <= 0 ||
                    item.Fields.GetValueOrDefault("ID")?.Text !=
                    item.ListItemId.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    throw new SnapshotIntegrityException("Selected item lacks its returned native identity.");
                if (batch.QueryKind != ListItemQueryKind.PointPublishingFiles && item.FileAssociation != null)
                    throw new SnapshotIntegrityException(
                        "An independent content item cannot acquire its renderer as a backing File.");
                if (item.FileAssociation is FileItemAssociation association)
                {
                    if (association.State.Succeeded)
                    {
                        PhysicalFileFacts file = association.FileFacts ??
                                                 throw new SnapshotIntegrityException(
                                                     "Successful File link has no native facts.");
                        if (file.SiteCollectionId != web.SiteCollectionId || file.WebId != web.WebId ||
                            file.ListId != batch.ListId || file.ListItemId != item.ListItemId ||
                            file.FileUniqueId == Guid.Empty)
                            throw new SnapshotIntegrityException(
                                "Selected item and physical File have different native ownership.");
                        RegisterFile(file, owner, links, assessmentId);
                    }
                    else if (association.FileFacts != null)
                        throw new SnapshotIntegrityException("Unproven File link cannot supply successful file facts.");
                }

                (Guid Site, Guid List, int Item) itemKey = (web.SiteCollectionId, batch.ListId, item.ListItemId);
                if (Items.TryGetValue(itemKey, out SelectedItem? previous))
                {
                    if (Fields(previous.Item) != Fields(item) ||
                        previous.Item.FileAssociation?.State != item.FileAssociation?.State ||
                        previous.Item.FileAssociation?.FileFacts?.FileUniqueId !=
                        item.FileAssociation?.FileFacts?.FileUniqueId)
                        throw new SnapshotIntegrityException(
                            "Repeated native item has conflicting fields or File association evidence.");
                }
                else
                    Items.Add(itemKey, new(batch, item));
            }
        }

        foreach (WebListItemQueries queries in ItemQueries.Values)
        foreach (ListItemQueryScope query in queries.Queries)
        {
            ListItemBatch[] batches = ItemBatches.Where(batch => batch.SiteUrl == queries.SiteUrl &&
                                                                 batch.WebUrl == queries.WebUrl &&
                                                                 batch.ListId == query.ListId)
                .OrderBy(batch => batch.PageOrdinal)
                .ToArray();
            if (batches.Length == 0 || batches.Where((batch, ordinal) => batch.PageOrdinal != ordinal).Any() ||
                batches[0].PagingToken != null || batches[^1].NextPage != null ||
                batches.Take(batches.Length - 1).Any(batch => batch.NextPage == null) ||
                batches.Skip(1).Where((batch, ordinal) => batch.PagingToken != batches[ordinal].NextPage).Any() ||
                batches.Take(batches.Length - 1).Select(batch => batch.NextPage).Distinct().Count() !=
                batches.Length - 1)
                throw new SnapshotIntegrityException(
                    "List-item acquisition has an incomplete or repeated native pagination chain.");
        }
    }

    /// <summary>
    /// Compares the exact typed item fields independently of dictionary insertion order.
    /// </summary>
    private static string Fields(ListItemFacts item) =>
        VersionedJson.From(new SortedDictionary<string, SourceField>(item.Fields, StringComparer.Ordinal)).Json;

    /// <summary>
    /// Requires fixed audit-window coverage and complete settled pagination for successful chunks.
    /// </summary>
    private void ValidateAudit(Dictionary<(int Chunk, int Page), (string Query, string? Next)> pages)
    {
        foreach (KeyValuePair<(int Chunk, int Page), (string Query, string? Next)> page in pages)
            if (!AuditChunks.TryGetValue(page.Key.Chunk, out ClassicPageAuditChunkSource? chunk) ||
                chunk.QueryId != page.Value.Query)
                throw new SnapshotIntegrityException("Audit response references an unrecorded query.");
        if (Scope.Options.SkipUsageInformation) return;
        DateTime cursor = Scope.AuditWindowStart;
        foreach (ClassicPageAuditChunkSource chunk in AuditChunks.Values.OrderBy(chunk => chunk.Start))
        {
            if (chunk.Start != cursor || chunk.End <= chunk.Start || chunk.End > Scope.AuditWindowEnd ||
                chunk.Status is not ("succeeded" or "failed" or "skipped"))
                throw new SnapshotIntegrityException("Audit chunks do not cover the pinned window exactly.");
            cursor = chunk.End;
            if (chunk.Status != "succeeded") continue;
            KeyValuePair<(int Chunk, int Page), (string Query, string? Next)>[] receipts =
                pages.Where(page => page.Key.Chunk == chunk.Chunk).OrderBy(page => page.Key.Page).ToArray();
            if (receipts.Length == 0 || receipts.Where((page, ordinal) => page.Key.Page != ordinal).Any() ||
                receipts[^1].Value.Next != null ||
                receipts.Take(receipts.Length - 1).Any(page => page.Value.Next == null))
                throw new SnapshotIntegrityException("Succeeded audit chunk has incomplete pagination receipts.");
        }

        if (cursor != Scope.AuditWindowEnd)
            throw new SnapshotIntegrityException("Audit window has an unrecorded acquisition gap.");
    }
}