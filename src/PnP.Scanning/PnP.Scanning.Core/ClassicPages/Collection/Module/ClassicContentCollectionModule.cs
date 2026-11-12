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
    private Guid assessmentId;
    public string ModuleKey => ClassicPageModule.Key;
    public string InputVersion => ClassicPageModule.InputVersion;

    /// <summary>
    /// Freezes typed Web scopes, then acquires each Web's file inventory, file enrichment, selected items, and audit receipts.
    /// </summary>
    public async IAsyncEnumerable<CollectionRecord> CollectAsync(
        CollectionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        assessmentId = context.AssessmentId;
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
        }

        ClassicCollectionScope scope = selection with { SiteCollections = siteCollections.ToArray() };
        await journal.ReadOrAcquireAsync(
            "CollectionScope",
            "assessment",
            () => Task.FromResult(scope),
            cancellationToken);
        ActionBlock<WebScope> workers = new(
            webScope => CollectWebAsync(webScope, scope, reader, journal, cancellationToken),
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

        if (!scope.Options.SkipUsageInformation) await reader.CollectAuditAsync(scope, journal, cancellationToken);
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
                async () =>
                {
                    if (!web.State.Succeeded) return UnavailableComponents(web, web.State);
                    try
                    {
                        return await reader.ReadClassicComponentsAsync(web, cancellationToken);
                    }
                    catch (Exception error) when (IsAcquisitionFailure(error, cancellationToken))
                    {
                        return UnavailableComponents(web, ClassicContentReader.Failure(error));
                    }
                },
                cancellationToken,
                value => AcquisitionJournal.Status(value.State),
                value => value.State.Error);
        foreach (DiscoveryEvidence candidate in inventory.Evidence.Where(evidence =>
                     evidence.RowType == DiscoveryRowType.Page))
            await journal.ReadOrAcquireAsync(
                "FileEnrichment",
                webKey + "|" + candidate.RecordKey,
                async () =>
                {
                    if (!web.State.Succeeded)
                        return FilePageSourceReader.UnavailableFileEnrichment(web, candidate, web.State);
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
    /// Limits the retained-component boundary to source acquisition faults; cancellation, local storage and
    /// snapshot-integrity errors still stop the assessment.
    /// </summary>
    private static bool IsAcquisitionFailure(Exception error, CancellationToken cancellationToken)
    {
        if (error is OperationCanceledException || cancellationToken.IsCancellationRequested) return false;
        for (Exception? current = error; current != null; current = current.InnerException)
            if (current is SnapshotIntegrityException or System.Data.Common.DbException
                or Microsoft.EntityFrameworkCore.DbUpdateException)
                return false;
        return true;
    }

    /// <summary>
    /// Retains an explicit failed or denied component receipt for one Web without fabricating component rows.
    /// </summary>
    private static ClassicComponentSource UnavailableComponents(WebFacts web, SourceReadState state) =>
        new(web.SiteUrl, web.WebUrl, state, new Dictionary<string, System.Text.Json.JsonElement[]>());

    /// <summary>Retains failed metadata and partial web-part acquisition without implying unread page content is complete.</summary>
    private static AcquisitionStatus FileEnrichmentStatus(FilePageEvidence source)
    {
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