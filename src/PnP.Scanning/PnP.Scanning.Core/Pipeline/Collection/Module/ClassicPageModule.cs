#nullable enable
using System.Runtime.CompilerServices;
using System.Threading.Tasks.Dataflow;
using PnP.Scanning.Core.Pipeline.Analysis.Module;
using PnP.Scanning.Core.Pipeline.Contracts.List;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Page;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Pipeline.Contracts.Site;
using PnP.Scanning.Core.Pipeline.Contracts.Web;
using PnP.Scanning.Core.Pipeline.Orchestration;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Services;

namespace PnP.Scanning.Core.Pipeline.Collection.Module;

/// <summary>
/// Registers versioned Classic object acquisition and Page analysis behind the existing Classic command.
/// </summary>
internal static class ClassicPageModule
{
    internal const string Key = ClassicContentContract.ModuleKey;
    internal const string InputVersion = ClassicContentContract.InputVersion;
    internal const string RuleVersion = ClassicContentContract.RuleVersion;

    /// <summary>
    /// Registers matching object contracts and rules; earlier snapshot shapes are rejected before execution rather than reinterpreted.
    /// </summary>
    internal static ModuleRegistry
        Registry(Func<CollectionContext, CancellationToken, Task<IClassicContentReader>>? online = null) =>
        new(
            [
                new CollectionRegistration(
                    Key,
                    InputVersion,
                    () => new ClassicContentCollectionModule(online),
                    ValidateParameters,
                    ValidateOptions)
            ],
            [
                new AnalysisRegistration(
                    Key,
                    RuleVersion,
                    [InputVersion],
                    () => new ClassicPageAnalysisModule(),
                    ClassicPageRuleMetadata.Validate,
                    ClassicPageRuleMetadata.Pin)
            ],
            new Dictionary<string, string> { [Key] = RuleVersion });

    /// <summary>
    /// Requires the typed-options collection parameter envelope without accepting unrecognized configuration.
    /// </summary>
    internal static void ValidateParameters(VersionedJson parameters)
    {
        if (parameters.SchemaVersion != 1 || parameters.Value.ValueKind != System.Text.Json.JsonValueKind.Object ||
            parameters.Value.EnumerateObject().Any())
            throw new ArgumentException(
                "Classic object collection uses typed Classic options; module parameters must be empty.");
    }

    /// <summary>
    /// Validates existing Pages selection, authentication metadata, property uniqueness, and audit bounds before acquisition.
    /// </summary>
    internal static void ValidateOptions(StartRequest request)
    {
        if (!Handles(request))
            throw new ArgumentException("The page pipeline requires Classic mode with the Pages component.");
        if (!Guid.TryParse(request.ApplicationId, out Guid application) || application == Guid.Empty)
            throw new ArgumentException("A nonempty application ID is required for Classic collection.");
        if (string.IsNullOrWhiteSpace(request.Tenant))
            throw new ArgumentException("A tenant is required for Classic collection.");
        if (!Enum.TryParse<Authentication.AuthenticationMode>(request.AuthMode, true, out _) ||
            !Enum.TryParse<global::PnP.Core.Services.Microsoft365Environment>(request.Environment, true, out _))
            throw new ArgumentException("A supported authentication mode and cloud environment are required.");
        if (request.Properties.GroupBy(property => property.Property, StringComparer.Ordinal)
            .Any(group => group.Count() > 1))
            throw new ArgumentException("Collection property names must be unique.");
        if (Options(request).AuditLogWindowDays is < 1 or > 180)
            throw new ArgumentException("Audit log window must be between 1 and 180 days.");
    }

    /// <summary>
    /// Reads fixed acquisition flags from persisted existing start properties.
    /// </summary>
    internal static ClassicCollectionOptions Options(StartRequest request)
    {
        bool Flag(string key) =>
            request.Properties.FirstOrDefault(property => property.Property == key)?.Value is string value &&
            bool.Parse(value);

        string? window = request.Properties
            .FirstOrDefault(property => property.Property == Constants.StartClassicAuditLogWindowDays)
            ?.Value;
        return new(
            Flag(Constants.StartClassicExportWebPartProperties),
            Flag(Constants.StartClassicSkipUsageInformation),
            Flag(Constants.StartClassicSkipUserInformation),
            Flag(Constants.StartClassicHomePageOnly),
            window == null ? 14 : int.Parse(window),
            request.Properties.FirstOrDefault(property => property.Property == Constants.StartExcludeSiteTemplates)
                ?.Value);
    }

    /// <summary>
    /// Selects the internal object pipeline for existing Classic requests that include Pages.
    /// </summary>
    internal static bool Handles(StartRequest request) =>
        request.Mode.Equals(Mode.Classic.ToString(), StringComparison.OrdinalIgnoreCase) &&
        OptionsBase.FromScannerInput(request) is ClassicOptions { Pages: true };

    /// <summary>
    /// Determines whether retained non-page collectors accompany Pages acquisition.
    /// </summary>
    internal static bool HasOtherComponents(StartRequest request) =>
        OptionsBase.FromScannerInput(request) is ClassicOptions options &&
        (options.Lists || options.InfoPath || options.Workflow || options.Extensibility);
}

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
            async webScope =>
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
                            return UnavailableWeb(
                                webScope,
                                Guid.Empty,
                                Guid.Empty,
                                ClassicContentReader.Failure(error));
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
                foreach (DiscoveryEvidence candidate in
                         inventory.Evidence.Where(evidence => evidence.RowType == "Page"))
                    await journal.ReadOrAcquireAsync(
                        "FileEnrichment",
                        webKey + "|" + candidate.RecordKey,
                        async () =>
                        {
                            if (!web.State.Succeeded)
                                return ClassicContentReader.UnavailableFileEnrichment(web, candidate, web.State);
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
                                return ClassicContentReader.UnavailableFileEnrichment(
                                    web,
                                    candidate,
                                    ClassicContentReader.Failure(error));
                            }
                        },
                        cancellationToken,
                        value => value.MetadataState.Succeeded
                            ? value.WebPartsState.Status is "Failed" or "Denied"
                                ? AcquisitionStatus.Partial
                                : AcquisitionStatus.Complete
                            : AcquisitionJournal.Status(value.MetadataState),
                        value => value.MetadataState.Error ?? value.WebPartsState.Error);
                WebListItemQueries queries = await journal.ReadOrAcquireAsync(
                    "ListItemQueries",
                    webKey,
                    async () =>
                    {
                        if (!web.State.Succeeded) return new(web.SiteUrl, web.WebUrl, [], web.State);
                        if (scope.Options.HomePageOnly)
                            return new(web.SiteUrl, web.WebUrl, [], SourceReadState.NotAttempted);
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
                            value => !value.State.Succeeded
                                ? AcquisitionJournal.Status(value.State)
                                : value.QueryKind == ListItemQueryKind.PointPublishingFiles && value.Items.Any(item =>
                                    item.FileAssociation is not { State.Succeeded: true })
                                    ? AcquisitionStatus.Partial
                                    : AcquisitionStatus.Complete,
                            value => value.State.Error ??
                                     value.Items.FirstOrDefault(item =>
                                             item.FileAssociation is { State.Succeeded: false })
                                         ?.FileAssociation?.State.Error ??
                                     (value.QueryKind == ListItemQueryKind.PointPublishingFiles &&
                                      value.Items.Any(item => item.FileAssociation == null)
                                         ? "Native File relationship evidence was not acquired."
                                         : null));
                        continuation = batch.NextPage;
                        if (continuation != null && !continuations.Add(continuation))
                            throw new SnapshotIntegrityException("List-item pagination repeated a continuation.");
                    } while (continuation != null);
                }
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

        if (!scope.Options.SkipUsageInformation) await reader.CollectAuditAsync(scope, journal, cancellationToken);
        yield return new(null, VersionedJson.From(new { stage = "ClassicObjectCollectionComplete" }));
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