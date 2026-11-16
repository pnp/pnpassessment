using PnP.Scanning.Core.ClassicPages.Collection.Page;
using PnP.Scanning.Core.ClassicPages.Contracts.Module;
using PnP.Scanning.Core.ClassicPages.Contracts.Shared;
using PnP.Scanning.Core.ClassicPages.Analysis.Module;
using PnP.Scanning.Core.Pipeline.Collection.Module;
#nullable enable
using System.Runtime.CompilerServices;
using System.Threading.Tasks.Dataflow;
using PnP.Scanning.Core.ClassicPages.Contracts.List;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.ClassicPages.Contracts.Page;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.ClassicPages.Contracts.Site;
using PnP.Scanning.Core.ClassicPages.Contracts.Web;


namespace PnP.Scanning.Core.ClassicPages.Collection.Module;

/// <summary>
/// Acquires object facts and discovery evidence with durable receipts; only analysis decides which objects become Pages.
/// </summary>
internal sealed class ClassicContentCollectionModule(
    Func<CollectionContext, CancellationToken, Task<IClassicContentReader>>? online = null)
    : ICollectionModule, ICollectionSnapshotValidator
{
    internal const string FindingSites = "Finding sites";
    internal const string ReadingSites = "Reading sites";
    internal const string ReadingUsage = "Reading usage data";
    private Guid assessmentId;
    public string ModuleKey => ClassicPageModule.Key;
    public string InputVersion => ClassicPageModule.InputVersion;

    /// <summary>
    /// Freezes typed Web scopes, then acquires each Web's file inventory, file enrichment, selected items, and audit receipts.
    /// Reports site collections expanded, then Webs read out of the frozen total, as live progress.
    /// The usage queries are sent once the scope is frozen and are checked while the Webs are read.
    /// </summary>
    public async IAsyncEnumerable<CollectionRecord> CollectAsync(
        CollectionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        assessmentId = context.AssessmentId;
        Action<PhaseProgress>? report = context.Progress;
        report?.Invoke(new(FindingSites));
        AcquisitionJournal journal = new(context);
        await journal.InitializeAsync(cancellationToken);
        await using IClassicContentReader reader = online == null
            ? new ClassicContentReader(context, await context.Environment.OpenAsync(context.Options, cancellationToken))
            : await online(context, cancellationToken);
        ClassicCollectionScope selection = await journal.ReadOrAcquireAsync(
            "CollectionPlan",
            "assessment",
            () => reader.ReadSiteSelectionAsync(ClassicPageModule.Options(context.Options), cancellationToken),
            cancellationToken);
        List<SiteCollectionScope> siteCollections = [];
        List<WebScope> webScopes = [];
        int siteCollectionsRead = 0;
        foreach (string siteCollectionUrl in selection.SiteCollectionUrls)
        {
            SiteCollectionScope siteScope = await journal.ReadOrAcquireAsync(
                "SiteCollectionScope",
                siteCollectionUrl.ToLowerInvariant(),
                () => reader.ReadWebScopesAsync(siteCollectionUrl, cancellationToken),
                cancellationToken,
                value => AcquisitionJournal.Status(value.State),
                value => value.State.Error);
            if (siteScope.Webs.Any(web => !string.Equals(
                    web.SiteCollectionUrl,
                    siteCollectionUrl,
                    StringComparison.OrdinalIgnoreCase)))
                throw new SnapshotIntegrityException("A Web scope belongs to a different site collection.");
            siteCollections.Add(siteScope);
            webScopes.AddRange(siteScope.Webs);
            report?.Invoke(
                new(FindingSites, ++siteCollectionsRead, selection.SiteCollectionUrls.Length, "site collections"));
        }

        ClassicCollectionScope scope = selection with { SiteCollections = siteCollections.ToArray() };
        await journal.ReadOrAcquireAsync(
            "CollectionScope",
            "assessment",
            () => Task.FromResult(scope),
            cancellationToken);
        // The audit service takes minutes to answer a usage query. The queries are sent before the first Web is
        // read and checked while the Webs are read, so that wait runs beside the reading. A new assessment has
        // signed in to find its Webs by now, so the usage read asks for its access after that sign-in.
        await using BackgroundRead? usage = scope.Options.SkipUsageInformation
            ? null
            : BackgroundRead.Start(token => reader.CollectAuditAsync(scope, journal, token), cancellationToken);
        int websTotal = webScopes.Count;
        int websRead = 0;
        object progressGate = new();
        report?.Invoke(new(ReadingSites, 0, websTotal, "webs"));
        ActionBlock<WebScope> workers = new(
            async webScope =>
            {
                await CollectWebAsync(webScope, scope, reader, journal, cancellationToken);
                // Count and report under one lock so parallel Webs never publish a lower count after a higher one.
                lock (progressGate) report?.Invoke(new(ReadingSites, ++websRead, websTotal, "webs"));
            },
            new ExecutionDataflowBlockOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = context.Options.Threads,
                BoundedCapacity = Math.Max(1, context.Options.Threads * 2),
            });
        try
        {
            foreach (WebScope webScope in webScopes)
                if (!await workers.SendAsync(webScope, cancellationToken))
                    break;
        }
        finally
        {
            workers.Complete();
            await workers.Completion;
        }

        if (usage != null)
        {
            // The Webs are read. What is left is the wait for usage queries that are still running.
            if (!usage.IsCompleted) report?.Invoke(new(ReadingUsage));
            await usage.WaitAsync();
        }

        yield return new(null, VersionedJson.From(new { stage = "ClassicObjectCollectionComplete" }));
    }

    /// <summary>
    /// Acquires one fixed Web's identity, file inventory, optional enrichment and paged native items with durable receipts.
    /// </summary>
    private static async Task CollectWebAsync(
        WebScope webScope,
        ClassicCollectionScope scope,
        IClassicContentReader reader,
        AcquisitionJournal journal,
        CancellationToken cancellationToken)
    {
        string webKey = CollectionJson.WebKey(webScope.SiteCollectionUrl, webScope.WebUrl);
        WebFacts identity = await journal.ReadOrAcquireAsync(
            "WebIdentity",
            webKey,
            async () =>
            {
                try
                {
                    return await reader.ReadIdentityAsync(webScope, cancellationToken);
                }
                catch (Exception error) when (error is not OperationCanceledException &&
                                              !cancellationToken.IsCancellationRequested)
                {
                    return UnavailableWeb(webScope, Guid.Empty, Guid.Empty, ClassicContentReader.Failure(error));
                }
            },
            cancellationToken,
            value => AcquisitionJournal.Status(value.State),
            value => value.State.Error);
        WebFileInventory inventory = identity.State.Succeeded
            ? await journal.ReadOrAcquireAsync(
                "FileInventory",
                webKey,
                () => reader.ReadFileInventoryAsync(identity, journal, cancellationToken),
                cancellationToken)
            : new(webScope.SiteCollectionUrl, webScope.WebUrl, [], []);
        WebFacts web = await journal.ReadOrAcquireAsync(
            "WebFacts",
            webKey,
            async () =>
            {
                if (!identity.State.Succeeded) return identity;
                try
                {
                    return await reader.ReadWebFactsAsync(webScope, cancellationToken);
                }
                catch (Exception error) when (error is not OperationCanceledException &&
                                              !cancellationToken.IsCancellationRequested)
                {
                    return UnavailableWeb(
                        webScope,
                        identity.SiteCollectionId,
                        identity.WebId,
                        ClassicContentReader.Failure(error));
                }
            },
            cancellationToken,
            value => AcquisitionJournal.Status(value.State),
            value => value.State.Error);
        if (scope.ClassicComponentsRequested)
            await journal.ReadOrAcquireAsync(
                "ClassicComponents",
                webKey,
                () => reader.ReadClassicComponentsAsync(web, cancellationToken),
                cancellationToken,
                value => AcquisitionJournal.Status(value.State),
                value => value.State.Error);
        HashSet<(Guid SiteCollectionId, Guid FileUniqueId)> listViewsAndForms = ListViewsAndForms(inventory);
        foreach (DiscoveryEvidence candidate in inventory.Evidence.Where(evidence =>
                     evidence.RowType == DiscoveryRowType.Page))
            await journal.ReadOrAcquireAsync(
                "FileEnrichment",
                webKey + "|" + candidate.RecordKey,
                async () =>
                {
                    if (!web.State.Succeeded)
                        return FilePageSourceReader.UnavailableFileEnrichment(web, candidate, web.State);
                    if (IsListViewOrFormWithoutItem(candidate, listViewsAndForms))
                        return FilePageSourceReader.ListViewOrFormEnrichment(web, candidate);
                    try
                    {
                        return await reader.ReadFileEnrichmentAsync(
                            web,
                            candidate,
                            scope.Options.SkipUserInformation,
                            cancellationToken);
                    }
                    catch (Exception error) when (error is not OperationCanceledException &&
                                                  !cancellationToken.IsCancellationRequested)
                    {
                        return FilePageSourceReader.UnavailableFileEnrichment(
                            web,
                            candidate,
                            ClassicContentReader.Failure(error));
                    }
                },
                cancellationToken,
                FileEnrichmentStatus,
                value => value.MetadataState.Error ?? value.WebPartsState.Error);
        WebListItemQueries queries = await journal.ReadOrAcquireAsync(
            "ListItemQueries",
            webKey,
            async () =>
            {
                if (!web.State.Succeeded) return new(web.SiteUrl, web.WebUrl, [], web.State);
                if (scope.Options.HomePageOnly) return new(web.SiteUrl, web.WebUrl, [], SourceReadState.NotAttempted);
                try
                {
                    return await reader.ReadListItemQueriesAsync(web, cancellationToken);
                }
                catch (Exception error) when (error is not OperationCanceledException &&
                                              !cancellationToken.IsCancellationRequested)
                {
                    return new(web.SiteUrl, web.WebUrl, [], ClassicContentReader.Failure(error));
                }
            },
            cancellationToken,
            value => AcquisitionJournal.Status(value.State),
            value => value.State.Error);
        foreach (ListItemQueryScope query in queries.Queries)
        {
            string? continuation = null;
            int ordinal = 0;
            HashSet<string> continuations = new(StringComparer.Ordinal);
            do
            {
                int batchOrdinal = ordinal++;
                string? requestContinuation = continuation;
                ListItemBatch batch = await journal.ReadOrAcquireAsync(
                    "ListItems",
                    webKey + "|" + query.ListId.ToString("D") + "|" + batchOrdinal,
                    async () =>
                    {
                        try
                        {
                            return (await reader.ReadListItemBatchAsync(
                                    web,
                                    query,
                                    requestContinuation,
                                    scope.Options.SkipUserInformation,
                                    cancellationToken)) with
                                {
                                    PageOrdinal = batchOrdinal, PagingToken = requestContinuation
                                };
                        }
                        catch (Exception error) when (error is not OperationCanceledException &&
                                                      !cancellationToken.IsCancellationRequested)
                        {
                            return new(
                                web.SiteUrl,
                                web.WebUrl,
                                query.ListId,
                                query.ListTitle,
                                query.ListUrl,
                                [],
                                null,
                                ClassicContentReader.Failure(error),
                                query.QueryKind,
                                query.ListTemplate,
                                query.DisplayFormUrl,
                                batchOrdinal,
                                requestContinuation,
                                query.ContentTypeForms);
                        }
                    },
                    cancellationToken,
                    value => AcquisitionJournal.Status(value.State),
                    value => value.State.Error);
                continuation = batch.NextPage;
                if (continuation != null && !continuations.Add(continuation))
                    throw new SnapshotIntegrityException("List-item pagination repeated a continuation.");
            } while (continuation != null);
        }
    }

    /// <summary>
    /// Names the files of one Web that its inventory recorded as a registered list view or list form.
    /// </summary>
    internal static HashSet<(Guid SiteCollectionId, Guid FileUniqueId)> ListViewsAndForms(WebFileInventory inventory)
    {
        HashSet<(Guid SiteCollectionId, Guid FileUniqueId)> files = [];
        foreach (PhysicalFileFacts file in inventory.Files ?? [])
        {
            if (file.AspxEvidenceJson == null) continue;
            // Only the two role flags are read, so the captured source bytes stay encoded.
            using System.Text.Json.JsonDocument evidence = System.Text.Json.JsonDocument.Parse(file.AspxEvidenceJson);
            if (IsTrue(evidence.RootElement, nameof(AspxPageEvidence.IsListView)) ||
                IsTrue(evidence.RootElement, nameof(AspxPageEvidence.IsListForm)))
                files.Add((file.SiteCollectionId, file.FileUniqueId));
        }

        return files;

        static bool IsTrue(System.Text.Json.JsonElement root, string name) =>
            root.ValueKind == System.Text.Json.JsonValueKind.Object &&
            root.TryGetProperty(name, out System.Text.Json.JsonElement value) &&
            value.ValueKind == System.Text.Json.JsonValueKind.True;
    }

    /// <summary>
    /// Recognizes a registered list view or list form that has no backing list item. It has no item fields to read
    /// and never becomes a Page, so its second read is recorded without contacting SharePoint. A view or form that
    /// is a list item keeps its item read, which is where its NoExecute value comes from.
    /// </summary>
    internal static bool IsListViewOrFormWithoutItem(
        DiscoveryEvidence candidate,
        HashSet<(Guid SiteCollectionId, Guid FileUniqueId)> listViewsAndForms) =>
        candidate.ListItemId == null && candidate.SiteCollectionId is Guid site &&
        candidate.FileUniqueId is Guid file && listViewsAndForms.Contains((site, file));

    /// <summary>
    /// Retains failed metadata and partial web-part acquisition without implying unread page content is complete.
    /// A registered list view or list form recorded without a second read is complete as recorded.
    /// </summary>
    internal static AcquisitionStatus FileEnrichmentStatus(FilePageEvidence source)
    {
        if (source.FileLevelOnly && source.WebPartsState.Status == ClassicAspxReadStates.NotRequested)
            return AcquisitionStatus.Complete;
        if (!source.MetadataState.Succeeded) return AcquisitionJournal.Status(source.MetadataState);
        return source.WebPartsState.Status is "Failed" or "Denied"
            ? AcquisitionStatus.Partial
            : AcquisitionStatus.Complete;
    }

    /// <summary>
    /// Validates relationships across the exact committed object receipts without mutating them or fabricating missing facts.
    /// </summary>
    public async Task ValidateAsync(ICollectionJournal journal, CancellationToken cancellationToken)
    {
        IReadOnlyList<CollectedObservation> receipts = await journal.ReadCommittedAsync(
            cancellationToken,
            metadataOnly: true);

        async IAsyncEnumerable<SourceRecord> Records()
        {
            foreach (CollectedObservation receipt in receipts)
            {
                if (CollectionJson.Kind(receipt) is not ("CollectionScope" or "WebFacts" or "FileInventory"
                    or "FileEnrichment" or "ListItemQueries" or "ListItems" or "AuditPage" or "AuditChunk"
                    or "ClassicComponents"))
                    continue;
                CollectedObservation value = await journal.ReadAsync(receipt.ObservationId, cancellationToken);
                yield return new(
                    value.ObservationId,
                    Guid.Empty,
                    value.SourceIdentity,
                    value.SourceRevision,
                    value.AcquisitionStatus,
                    value.Metadata,
                    new SourceArtifact(value.RawBytes?.LongLength, null, value.RawBytes),
                    value.AcquisitionError);
            }
        }

        await ClassicContentIndex.CreateAsync(Records(), cancellationToken, assessmentId);
    }

    /// <summary>
    /// Retains an unavailable Web read under its fixed locator and observed template; it never creates physical file identity.
    /// </summary>
    private static WebFacts UnavailableWeb(WebScope scope, Guid siteId, Guid webId, SourceReadState state) =>
        new(
            scope.SiteCollectionUrl,
            scope.WebUrl,
            scope.WebTemplate,
            siteId,
            webId,
            default,
            [],
            [],
            null,
            SourceReadState.NotAttempted,
            null,
            SourceReadState.NotAttempted,
            null,
            0,
            null,
            SourceReadState.NotAttempted,
            state);
}