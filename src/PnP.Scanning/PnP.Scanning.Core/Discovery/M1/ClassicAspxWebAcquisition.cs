using PnP.Scanning.Core.Storage;
using System.Runtime.CompilerServices;
using System.Text.Json;
using static PnP.Scanning.Core.Discovery.SharePointAspxRestJson;
using static PnP.Scanning.Core.Discovery.SharePointAspxUrlPolicy;

namespace PnP.Scanning.Core.Discovery.M1;
/// <summary>Incremental native acquisition only. Persistence/union and scan scheduling belong to the caller.</summary>
internal sealed class ClassicAspxWebAcquisition
{
    private const string ServerGuidFormat = "D";
    private const string ScopeValidationMessage = "Exact server IDs, web/client scope and bounded acquisition limits are required.";
    private const string BearerOrJwtPattern = @"\bBearer\s+\S+|\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b";
    private const string SecretKeyValuePattern = @"(access_token|refresh_token|client_secret|password)\s*[:=]\s*[^&\s;]+";
    private const string RedactedText = "[redacted]";
    private const string RedactedKeyValue = "$1=[redacted]";
    private const int MaximumDiagnosticDetailLength = 1024;
    private readonly M1WebAcquisitionContext context;
    private readonly ISharePointAspxRestClient client;
    private readonly ISharePointAspxRestClientFactory clientFactory;
    private ISharePointAspxRestClient siteRootClient;
    private readonly SharePointAspxUrlPolicy urlPolicy;
    private readonly List<ClassicAspxInventory> inventory = new();
    private readonly List<ClassicAspxItemBinding> bindings = new();
    private readonly List<ClassicPageDiscovery> evidence = new();
    private readonly Dictionary<string, ClassicAspxInventory> resources = new(StringComparer.OrdinalIgnoreCase);
    private Func<M1AcquisitionBatch, CancellationToken, Task> publish;
    private int BufferedRowCount => inventory.Count + bindings.Count + evidence.Count;

    /// <summary>Preserves the legacy site-relative Web path used by raw acquisition evidence.</summary>
    private string SiteRelativeEvidenceWebPath
    {
        get
        {
            string webPath = context.WebUrl.AbsolutePath.TrimEnd('/');
            string sitePath = context.SiteUrl.AbsolutePath.TrimEnd('/');
            string relativePath = webPath[sitePath.Length..];
            return relativePath.Length > 0 ? relativePath : "/";
        }
    }

    /// <summary>
    /// Captures one Web acquisition scope and borrowed clients, validating identity and finite limits before any
    /// request is sent.
    /// </summary>
    internal ClassicAspxWebAcquisition(
        M1WebAcquisitionContext context,
        ISharePointAspxRestClient client,
        ISharePointAspxRestClientFactory clientFactory = null)
    {
        this.context = context ?? throw new ArgumentNullException(nameof(context));
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.clientFactory = clientFactory;
        urlPolicy = new SharePointAspxUrlPolicy(context.SiteUrl, context.WebUrl);
        SharePointFileContentReader.ValidateWeb(context.SiteUrl);
        SharePointFileContentReader.ValidateWeb(context.WebUrl);
        SharePointFileContentReader.ValidateWeb(client.WebUrl);
        if (context.ScanId == Guid.Empty || context.SiteCollectionId == Guid.Empty
            || context.WebId == Guid.Empty
            || !HasSameHttpsAuthority(context.SiteUrl, context.WebUrl)
            || !HasSameHttpsAuthority(context.WebUrl, client.WebUrl)
            || context.WebUrl.AbsolutePath.TrimEnd('/') != client.WebUrl.AbsolutePath.TrimEnd('/')
            || !IsPathWithinRoot(context.WebUrl.AbsolutePath, context.SiteUrl.AbsolutePath)
            || context.BatchSize is < 1 or > 200
            || context.MaxCollectionPages < 1
            || context.MaxContentTypes < 1
            || context.MaxResourceCacheEntries < 1
            || context.MaxFileBytes is < 1 or > 16 * 1024 * 1024)
        {
            throw new ArgumentException(ScopeValidationMessage);
        }
    }

    /// <summary>
    /// Imports physical ASPX observations, then walks list content types and items within this Web's declared scope.
    /// Publishes bounded observation batches; cancellation and persistence failures propagate to the native worker.
    /// </summary>
    internal async Task AcquireAsync(
        IAsyncEnumerable<ClassicPageDiscovery> physicalPages,
        Func<M1AcquisitionBatch, CancellationToken, Task> publish,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(physicalPages);
        this.publish = publish ?? throw new ArgumentNullException(nameof(publish));
        await ImportPhysicalObservationsAsync(physicalPages, cancellationToken);
        if (context.Options?.HomePageOnly == true)
        {
            await BufferEvidenceAsync(
                CreateEvidenceRow(
                    ClassicAspxAcquisitionEvidenceNames.Scope,
                    ClassicAspxAcquisitionEvidenceNames.Surface,
                    ClassicAspxAcquisitionScopes.HomePageItems,
                    DiscoveryTerminalOutcome.PolicyExcluded,
                    ClassicAspxAcquisitionErrorCodes.HomePageItemsExcluded),
                cancellationToken);
            await FlushAsync(cancellationToken);
            return;
        }

        await AcquireListsAsync(cancellationToken);
        await FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Records real physical identities without treating a legacy scheduling WebId or renderer pointer as
    /// backing-item proof.
    /// A targeted homepage may therefore retain an unconfirmed-backing gap instead of inventing an Item relation.
    /// </summary>
    private async Task ImportPhysicalObservationsAsync(IAsyncEnumerable<ClassicPageDiscovery> physicalPages, CancellationToken cancellationToken)
    {
        await foreach (var observation in physicalPages.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (observation.RowType != ClassicAspxAcquisitionEvidenceNames.Page)
            {
                continue;
            }

            if (observation.ScanId != context.ScanId || observation.SiteCollectionId != context.SiteCollectionId
                || !HasNonEmptyGuid(observation.FileUniqueId)
                || !urlPolicy.TryNormalizeResourceReference(observation.Url, out var path)
                || !IsAspxPath(path))
            {
                await BufferGapAsync(
                    ClassicAspxAcquisitionScopes.Physical,
                    ClassicAspxAcquisitionErrorCodes.PhysicalIdentityUnproved,
                    DiscoveryTerminalOutcome.Unknown,
                    cancellationToken);
                continue;
            }

            var file = new SharePointResolvedFile(
                DiscoveryTerminalOutcome.Complete,
                observation.FileUniqueId.Value.ToString(ServerGuidFormat),
                observation.FileName,
                path,
                observation.CustomizedPageStatus,
                observation.ObservationMethod ?? ClassicAspxAcquisitionSources.PhysicalSource,
                ListId: observation.ListId,
                ListItemId: observation.ListItemId,
                ContentTypeId: observation.ContentTypeId);
            await ObserveTenantFileAsync(file, path, ClassicAspxAcquisitionSources.PhysicalFile, observation, cancellationToken);
            if (context.Options?.HomePageOnly == true && HasNonEmptyGuid(observation.ListId)
                && observation.ListItemId > 0)
            {
                await BufferGapAsync(
                    ClassicAspxAcquisitionScopes.HomePageOnly,
                    ClassicAspxAcquisitionErrorCodes.HomePageBackingUnconfirmed,
                    DiscoveryTerminalOutcome.Unknown,
                    cancellationToken);
            }
        }
    }

    /// <summary>
    /// Enumerates every authorized list and completes its content-type attempt before processing that list's item
    /// stream.
    /// Hidden flags, list base types and content-type families do not filter this discovery authority.
    /// </summary>
    private async Task AcquireListsAsync(CancellationToken cancellationToken)
    {
        await foreach (var list in ReadCollectionPagesAsync(
            ClassicAspxAcquisitionScopes.Lists,
            CreateEndpointUri(SharePointAspxRestQueries.Lists),
            cancellationToken))
        {
            var listId = ReadNonEmptyGuid(list, SharePointAspxRestFields.Id);
            if (!HasNonEmptyGuid(listId))
            {
                await BufferGapAsync(
                    ClassicAspxAcquisitionScopes.Lists,
                    ClassicAspxAcquisitionErrorCodes.MissingListGuid,
                    DiscoveryTerminalOutcome.Unknown,
                    cancellationToken);
                continue;
            }

            var contentTypes = await ReadListContentTypesAsync(listId.Value, cancellationToken);
            await AcquireListItemsAsync(listId.Value, list, contentTypes, cancellationToken);
        }
    }

    /// <summary>
    /// Reads bounded list-local content-type metadata while preserving loaded, empty and unknown renderer values.
    /// Capped, missing or duplicate identities invalidate the snapshot, so item processing cannot grant a fallback.
    /// </summary>
    private async Task<ContentTypeMetadataSnapshot> ReadListContentTypesAsync(Guid listId, CancellationToken cancellationToken)
    {
        var entries = new List<ListContentTypeMetadata>();
        bool isComplete = false;
        string scope = ClassicAspxAcquisitionScopes.ContentTypesPrefix + listId.ToString(ServerGuidFormat);
        await foreach (var contentType in ReadCollectionPagesAsync(
            scope,
            CreateEndpointUri(SharePointAspxRestQueries.ForContentTypes(listId)),
            cancellationToken,
            completed => isComplete = completed))
        {
            if (entries.Count >= context.MaxContentTypes)
            {
                await BufferGapAsync(
                    scope,
                    ClassicAspxAcquisitionErrorCodes.ContentTypeLimit,
                    DiscoveryTerminalOutcome.Truncated,
                    cancellationToken);
                isComplete = false;
                break;
            }

            string contentTypeId = ReadContentTypeId(contentType, SharePointAspxRestFields.Id);
            if (string.IsNullOrWhiteSpace(contentTypeId))
            {
                isComplete = false;
                entries.Add(new ListContentTypeMetadata(null, null, false));
            }
            else
            {
                entries.Add(new ListContentTypeMetadata(
                        contentTypeId,
                        ReadOptionalString(contentType, SharePointAspxRestFields.DisplayFormUrl),
                        TryReadProperty(contentType, SharePointAspxRestFields.DisplayFormUrl, out _)));
            }
        }

        bool hasUnprovedIdentity = entries.Any(entry => entry.Id == null);
        bool hasDuplicateIdentity = !hasUnprovedIdentity
            && entries.GroupBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1);
        if (hasUnprovedIdentity || hasDuplicateIdentity)
        {
            isComplete = false;
            await BufferGapAsync(
                scope,
                ClassicAspxAcquisitionErrorCodes.AmbiguousContentType,
                DiscoveryTerminalOutcome.Unknown,
                cancellationToken);
        }

        return new ContentTypeMetadataSnapshot(entries.AsReadOnly(), isComplete);
    }

    /// <summary>
    /// Streams positive-ID items one page at a time and observes file-self, display-renderer and layout relations in
    /// order.
    /// The optional item GUID is supplementary; its absence never excludes a real scoped integer Item identity.
    /// </summary>
    private async Task AcquireListItemsAsync(
        Guid listId,
        JsonElement list,
        ContentTypeMetadataSnapshot contentTypes,
        CancellationToken cancellationToken)
    {
        string scope = ClassicAspxAcquisitionScopes.ItemsPrefix + listId.ToString(ServerGuidFormat);
        await foreach (var item in ReadCollectionPagesAsync(scope, CreateEndpointUri(SharePointAspxRestQueries.ForItems(listId)), cancellationToken))
        {
            int? itemId = ReadOptionalInt32(item, SharePointAspxRestFields.Id);
            if (itemId is null or <= 0)
            {
                await BufferGapAsync(
                    scope,
                    ClassicAspxAcquisitionErrorCodes.MissingPositiveItemId,
                    DiscoveryTerminalOutcome.Unknown,
                    cancellationToken);
                continue;
            }

            var observation = ReadItemObservation(item, listId, itemId.Value);
            await ObserveItemFileAsync(observation, cancellationToken);
            await ObserveItemRendererAsync(observation, list, contentTypes, scope, cancellationToken);
            await ObservePublishingLayoutAsync(observation, cancellationToken);
        }
    }

    /// <summary>Groups one server-observed Item's identity and metadata without materializing any other item page.</summary>
    private static ListItemObservation ReadItemObservation(JsonElement item, Guid listId, int itemId)
    {
        Guid? itemGuid = ReadNonEmptyGuid(item, SharePointAspxRestFields.ItemGuid);
        string title = ReadOptionalString(item, SharePointAspxRestFields.Title);
        string contentTypeId = ReadContentTypeId(item, SharePointAspxRestFields.ContentTypeId);
        string itemUrl = ReadOptionalString(item, SharePointAspxRestFields.FileRef);
        bool ownsFile = TryReadProperty(item, SharePointAspxRestFields.File, out var file)
            && HasNonEmptyGuid(ReadNonEmptyGuid(file, SharePointAspxRestFields.UniqueId));
        return new ListItemObservation
        {
            RawItem = item,
            ListId = listId,
            ItemId = itemId,
            ItemGuid = itemGuid,
            Title = title,
            ContentTypeId = contentTypeId,
            ItemUrl = itemUrl,
            OwnsFile = ownsFile,
            File = file,
            FilePath = ownsFile ? ReadOptionalString(file, SharePointAspxRestFields.ServerRelativeUrl) : null

        };
    }

    /// <summary>
    /// Adds FileSelf only for an actual expanded ASPX File, or a resolved file with the exact current ListId and
    /// ItemId.
    /// A path that merely looks like an ASPX renderer is not backing-item ownership evidence.
    /// </summary>
    private async Task ObserveItemFileAsync(ListItemObservation item, CancellationToken cancellationToken)
    {
        if (item.OwnsFile)
        {
            if (urlPolicy.TryNormalizeResourceReference(item.FilePath, out var path) && IsAspxPath(path))
            {
                var file = new SharePointResolvedFile(
                    DiscoveryTerminalOutcome.Complete,
                    ReadNonEmptyGuid(item.File, SharePointAspxRestFields.UniqueId).Value.ToString(ServerGuidFormat),
                    ReadOptionalString(item.File, SharePointAspxRestFields.Name),
                    path,
                    null,
                    ClassicAspxAcquisitionSources.RestItemFileExpansion,
                    ListId: item.ListId,
                    ListItemId: item.ItemId,
                    ContentTypeId: item.ContentTypeId);
                var resource = await ObserveTenantFileAsync(file, path, ClassicAspxAcquisitionSources.ItemFile, null, cancellationToken, context.WebId);
                if (resource != null)
                {
                    await BufferItemBindingAsync(
                        resource,
                        item,
                        ClassicAspxBindingKinds.FileSelf,
                        ClassicAspxAcquisitionSources.ItemFileExpansion,
                        cancellationToken);
                }
            }

            return;
        }

        if (!urlPolicy.TryNormalizeResourceReference(item.ItemUrl, out var filePath) || !IsAspxPath(filePath))
        {
            return;
        }

        var resolved = await ResolveTenantFileAsync(filePath, cancellationToken);
        if (resolved == null || resolved.ListId != item.ListId || resolved.ListItemId != item.ItemId)
        {
            return;
        }

        var backing = await ObserveTenantFileAsync(resolved, filePath, ClassicAspxAcquisitionSources.ResolvedItemFile, null, cancellationToken);
        if (backing != null)
        {
            await BufferItemBindingAsync(
                backing,
                item,
                ClassicAspxBindingKinds.FileSelf,
                ClassicAspxAcquisitionSources.ExactBackingItem,
                cancellationToken);
        }
    }

    /// <summary>
    /// Resolves the most-specific known CT display override, or its known-empty override's list default.
    /// Incomplete CT authority or unloaded fields remain gaps; an ancestor override is never substituted.
    /// </summary>
    private async Task ObserveItemRendererAsync(
        ListItemObservation item,
        JsonElement list,
        ContentTypeMetadataSnapshot contentTypes,
        string itemScope,
        CancellationToken cancellationToken)
    {
        if (!contentTypes.IsComplete)
        {
            await BufferGapAsync(
                itemScope,
                ClassicAspxAcquisitionErrorCodes.IncompleteContentTypes,
                DiscoveryTerminalOutcome.Unknown,
                cancellationToken);
            return;
        }

        var matches = contentTypes.Entries
            .Where(entry => entry.Id != null && item.ContentTypeId != null &&
                item.ContentTypeId.StartsWith(entry.Id, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.Id.Length)
            .ToArray();
        if (matches.Length == 0 || !matches[0].IsDisplayFormLoaded)
        {
            await BufferGapAsync(
                itemScope,
                ClassicAspxAcquisitionErrorCodes.ItemRendererUnproved,
                DiscoveryTerminalOutcome.Unknown,
                cancellationToken);
            return;
        }

        var selected = matches[0];
        bool hasOverride = !string.IsNullOrWhiteSpace(selected.DisplayFormUrl);
        string renderer = hasOverride ? selected.DisplayFormUrl : ReadOptionalString(list, SharePointAspxRestFields.DefaultDisplayFormUrl);
        if (!hasOverride && !TryReadProperty(list, SharePointAspxRestFields.DefaultDisplayFormUrl, out _))
        {
            await BufferGapAsync(
                itemScope,
                ClassicAspxAcquisitionErrorCodes.ListRendererUnloaded,
                DiscoveryTerminalOutcome.Unknown,
                cancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(renderer))
        {
            return;
        }

        var resource = await ResolveResourceAsync(
            renderer,
            hasOverride ? ClassicAspxAcquisitionSources.ContentTypeDisplayForm : ClassicAspxAcquisitionSources.ListDisplayForm,
            cancellationToken);
        var kind = hasOverride ? ClassicAspxBindingKinds.ContentTypeRenderer : ClassicAspxBindingKinds.ListFormRenderer;
        if (item.OwnsFile && !IsAspxPath(item.FilePath))
        {
            kind |= ClassicAspxBindingKinds.DocumentMetadataRenderer;
        }

        if (resource != null)
        {
            await BufferItemBindingAsync(
                resource,
                item,
                kind,
                hasOverride ? ClassicAspxAcquisitionSources.MostSpecificContentTypeDisplayForm : ClassicAspxAcquisitionSources.ListDisplayForm,
                cancellationToken);
        }
    }

    /// <summary>Observes the explicitly stored publishing-layout URL in addition to the item's other supported routes.</summary>
    private async Task ObservePublishingLayoutAsync(ListItemObservation item, CancellationToken cancellationToken)
    {
        string layout = ReadUrlFieldValue(item.RawItem, SharePointAspxRestFields.PublishingPageLayout);
        if (string.IsNullOrWhiteSpace(layout))
        {
            return;
        }

        var resource = await ResolveResourceAsync(layout, ClassicAspxAcquisitionSources.PublishingLayout, cancellationToken);
        if (resource != null)
        {
            await BufferItemBindingAsync(
                resource,
                item,
                ClassicAspxBindingKinds.PublishingPageLayout,
                ClassicAspxAcquisitionSources.ItemPublishingLayout,
                cancellationToken);
        }
    }

    /// <summary>
    /// Buffers one observed Item/resource pair with its real Item Web scope and independent acquisition reason.
    /// Multiple reasons are intentionally separate observations here; the projection writer merges them by the
    /// normalized pair key.
    /// </summary>
    private async Task BufferItemBindingAsync(
        ClassicAspxInventory resource,
        ListItemObservation item,
        ClassicAspxBindingKinds kind,
        string source,
        CancellationToken cancellationToken)
    {
        bindings.Add(new ClassicAspxItemBinding
        {
            ScanId = context.ScanId,
            AspxKey = resource.AspxKey,
            SiteCollectionId = context.SiteCollectionId,
            WebId = context.WebId,
            ListId = item.ListId,
            ItemId = item.ItemId,
            ItemGuid = HasNonEmptyGuid(item.ItemGuid) ? item.ItemGuid : null,
            ItemGuidReadState = HasNonEmptyGuid(item.ItemGuid) ? ClassicAspxReadStates.Available : ClassicAspxReadStates.Unavailable,
            SiteUrl = context.SiteUrl.AbsoluteUri,
            WebUrl = context.WebUrl.AbsoluteUri,
            ItemUrl = item.ItemUrl,
            ItemTitle = item.Title,
            ContentTypeId = item.ContentTypeId,
            BindingKinds = kind,
            ObservedAtUtc = DateTime.UtcNow,
            BindingProvenanceJson = JsonSerializer.Serialize(new[] { new {
                            source,
                            realItemId = item.ItemId
                        } })
        });
        await FlushIfBatchFullAsync(cancellationToken);
    }

    /// <summary>
    /// Streams authenticated collection pages in server order and records each request receipt. Only a validated
    /// terminal chain reports complete; cancellation and persistence failures are never converted into empty
    /// collections.
    /// </summary>
    private async IAsyncEnumerable<JsonElement> ReadCollectionPagesAsync(
        string scope,
        Uri first,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        Action<bool> completed = null)
    {
        var receipts = new List<AspxPaginationPageReceipt>();
        var seen = new HashSet<string>(StringComparer.Ordinal)
        {
            first.AbsoluteUri
        };
        Uri next = first;
        string token = null;
        int observed = 0;
        var outcome = DiscoveryTerminalOutcome.Unknown;
        for (int ordinal = 0; next != null && ordinal < context.MaxCollectionPages; ordinal++)
        {
            SharePointRestPage page;
            try
            {
                page = await client.GetPageAsync(next, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                page = new(
                    next,
                    0,
                    Array.Empty<JsonElement>(),
                    null,
                    DiscoveryHash.Of(ex.GetType().FullName),
                    ClassicAspxAcquisitionEvidenceNames.TransportErrorSchema,
                    DiscoveryTerminalOutcome.Failed,
                    ClassicAspxAcquisitionErrorCodes.TransportFailure,
                    ReceivedAtUtc: DateTimeOffset.UtcNow);
            }

            bool uriValid = page.RequestUri != null && page.RequestUri.AbsoluteUri == next.AbsoluteUri;
            outcome = uriValid ? page.Outcome : DiscoveryTerminalOutcome.Unknown;
            bool terminal = string.IsNullOrWhiteSpace(page.NextLink);
            var receipt = new AspxPaginationPageReceipt(
                AspxAcquisitionVersions.PaginationReceipt,
                scope,
                context.AuthorityRevision,
                DiscoveryHash.Of(first.GetLeftPart(UriPartial.Path), first.Query),
                ClassicAspxAcquisitionEvidenceNames.GetMethod,
                AspxDurableRequestEvidence.Endpoint(page.RequestUri ?? next),
                first.Query,
                string.Empty,
                ordinal,
                AspxPaginationContract.TokenHash(token),
                page.Items.Count,
                AspxPaginationContract.TokenHash(page.NextLink),
                page.ResponseDigest,
                page.StatusCode == 0 ? null : (int)page.StatusCode,
                page.SemanticDetectorResult,
                page.AttemptCount,
                page.AttemptLimit,
                page.RequestId,
                page.CorrelationId,
                uriValid ? page.ErrorCode : ClassicAspxAcquisitionErrorCodes.ResponseUriDrift,
                terminal,
                page.ReceivedAtUtc == default ? DateTimeOffset.UtcNow : page.ReceivedAtUtc);
            receipts.Add(receipt);
            await BufferEvidenceAsync(
                CreateEvidenceRow(
                    ClassicAspxAcquisitionEvidenceNames.Pagination,
                    ClassicAspxAcquisitionEvidenceNames.RequestPage,
                    scope + ":" + ordinal,
                    outcome,
                    receipt.ErrorCode,
                    JsonSerializer.Serialize(receipt)),
                cancellationToken);
            if (outcome != DiscoveryTerminalOutcome.Complete)
            {
                break;
            }

            foreach (var row in page.Items)
            {
                observed++;
                yield return row;
            }

            if (terminal)
            {
                next = null;
                break;
            }

            if (!TryValidateContinuationUri(first, page.NextLink, out var resolved)
                || !seen.Add(resolved.AbsoluteUri))
            {
                outcome = DiscoveryTerminalOutcome.Unknown;
                await BufferGapAsync(scope, ClassicAspxAcquisitionErrorCodes.UnsafeOrRepeatedCursor, outcome, cancellationToken);
                break;
            }

            token = page.NextLink;
            next = resolved;
        }

        if (next != null && outcome == DiscoveryTerminalOutcome.Complete)
        {
            outcome = DiscoveryTerminalOutcome.Truncated;
        }

        var validation = AspxPaginationContract.Validate(receipts, outcome);
        bool success = next == null
            && validation.Outcome is DiscoveryTerminalOutcome.Complete or DiscoveryTerminalOutcome.Empty;
        completed?.Invoke(success);
        var state = success && observed == 0 ? DiscoveryTerminalOutcome.Empty : success ? DiscoveryTerminalOutcome.Complete : outcome;
        if (!success && state is DiscoveryTerminalOutcome.Complete or DiscoveryTerminalOutcome.Empty)
        {
            state = DiscoveryTerminalOutcome.Unknown;
        }

        await BufferEvidenceAsync(
            CreateEvidenceRow(
                ClassicAspxAcquisitionEvidenceNames.Scope,
                ClassicAspxAcquisitionEvidenceNames.Surface,
                scope,
                state,
                success ? null : ClassicAspxAcquisitionErrorCodes.IncompleteCollection,
                JsonSerializer.Serialize(new
                {
                    observed,
                    validation.ChainHash,
                    validation.OutstandingTokenCount,
                    validation.GapCodes,
                    ExpectedCount = success ? (int?)observed : null,
                    TerminalComplete = success
                })),
            cancellationToken);
    }

    /// <summary>
    /// Resolves one ASPX reference through the bounded resource cache, unique frozen catalog membership, or a real
    /// tenant File identity. Unresolved and ambiguous references remain gaps rather than Item bindings.
    /// </summary>
    private async Task<ClassicAspxInventory> ResolveResourceAsync(
        string reference,
        string source,
        CancellationToken cancellationToken)
    {
        if (!urlPolicy.TryNormalizeResourceReference(reference, out var path) || !IsAspxPath(path))
        {
            await BufferGapAsync(
                source,
                ClassicAspxAcquisitionErrorCodes.UnsupportedRendererPath,
                DiscoveryTerminalOutcome.Unknown,
                cancellationToken);
            return null;
        }

        if (resources.TryGetValue(path, out var cached))
        {
            return cached;
        }

        if (IsLayoutsPath(path))
        {
            IReadOnlyList<PlatformAspxCatalogEntry> entries = Array.Empty<PlatformAspxCatalogEntry>();
            try
            {
                if (context.Catalog != null)
                {
                    entries = context.Catalog.ResolvePath(path);
                }
            }
            catch (ArgumentException)
            {
            }

            if (entries.Count != 1)
            {
                await BufferGapAsync(
                    source,
                    entries.Count == 0 ? ClassicAspxAcquisitionErrorCodes.UnresolvedCatalogMember : ClassicAspxAcquisitionErrorCodes.AmbiguousCatalogAlias,
                    DiscoveryTerminalOutcome.Unknown,
                    cancellationToken);
                return null;
            }

            var entry = entries[0];
            var result = new ClassicAspxInventory
            {
                ScanId = context.ScanId,
                AspxKey = ClassicAspxInventory.CreatePlatformKey(context.Catalog.Source, context.Catalog.Version, context.Catalog.Digest, entry.AspxKey),
                IdentityKind = ClassicAspxIdentityKinds.PlatformCatalog,
                Url = entry.Path,
                FileName = entry.Path.Split('/')[^1],
                CatalogSource = context.Catalog.Source,
                CatalogVersion = context.Catalog.Version,
                CatalogDigest = context.Catalog.Digest,
                CatalogEntryKey = entry.AspxKey,
                FileReadState = ClassicAspxReadStates.NotApplicable,
                HandlerReadState = entry.DeclaredInherits == null ? ClassicAspxReadStates.NotRequested : ClassicAspxReadStates.Available,
                HandlerParseState = entry.DeclaredInherits == null ? nameof(AspxHandlerParseState.NotParsed) : nameof(AspxHandlerParseState.Declared),
                HandlerSource = ClassicAspxHandlerSources.VersionedCatalogReference,
                HandlerDeclaredType = entry.DeclaredInherits,
                HandlerNormalizedType = entry.DeclaredInherits?.Trim(),
                HandlerEffectiveType = entry.DeclaredInherits?.Trim(),
                CoverageState = ClassicAspxCoverageStates.VersionedReference,
                FirstObservedAtUtc = DateTime.UtcNow,
                ObservedAtUtc = DateTime.UtcNow,
                ProvenanceJson = JsonSerializer.Serialize(new[] { new {
                                source,
                                path,
                                entry.HandlerEvidenceSource
                            } })

            };
            CacheResource(path, result);
            await BufferInventoryAsync(result, cancellationToken);
            return result;
        }

        var file = await ResolveTenantFileAsync(path, cancellationToken);
        return file == null ? null : await ObserveTenantFileAsync(file, path, source, null, cancellationToken);
    }

    /// <summary>
    /// Requires a successful metadata read with a real File GUID and the same normalized path, using the current Web
    /// or verified site-root client.
    /// </summary>
    private async Task<SharePointResolvedFile> ResolveTenantFileAsync(string path, CancellationToken cancellationToken)
    {
        var selected = await GetFileClientForPathAsync(path, cancellationToken);
        if (selected == null)
        {
            return null;
        }

        SharePointResolvedFile file;
        try
        {
            file = await selected.ResolveFileAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await BufferFileGapAsync(
                path,
                ClassicAspxAcquisitionEvidenceNames.FileResolution,
                ClassicAspxAcquisitionErrorCodes.FileResolutionPrefix + ex.GetType().Name,
                DiscoveryTerminalOutcome.Failed,
                cancellationToken,
                ex.GetType().FullName,
                resolverWeb: selected.WebUrl);
            return null;
        }

        if (file.Outcome != DiscoveryTerminalOutcome.Complete || !Guid.TryParse(file.FileUniqueId, out var id)
            || id == Guid.Empty
            || !urlPolicy.TryNormalizeResourceReference(file.ServerRelativeUrl, out var actual)
            || !string.Equals(path, actual, StringComparison.OrdinalIgnoreCase))
        {
            await BufferFileGapAsync(
                path,
                file.ErrorStage ?? ClassicAspxAcquisitionEvidenceNames.FileResolution,
                file.ErrorCode ?? ClassicAspxAcquisitionErrorCodes.UnprovedFileIdentity,
                file.Outcome == DiscoveryTerminalOutcome.Denied ? file.Outcome : DiscoveryTerminalOutcome.Unknown,
                cancellationToken,
                file.ErrorDetail,
                file.EvidenceRef,
                selected.WebUrl);
            return null;
        }

        return file;
    }

    /// <summary>
    /// Reads bounded source through the selected file client and records inventory plus Handler evidence. Metadata
    /// discovery survives a denied or incomplete source read without gaining a guessed Handler or file owner.
    /// </summary>
    private async Task<ClassicAspxInventory> ObserveTenantFileAsync(
        SharePointResolvedFile file,
        string path,
        string source,
        ClassicPageDiscovery observation,
        CancellationToken cancellationToken,
        Guid? owningWeb = null)
    {
        if (!Guid.TryParse(file.FileUniqueId, out var fileId) || fileId == Guid.Empty)
        {
            return null;
        }

        var selected = await GetFileClientForPathAsync(path, cancellationToken);
        SharePointFileContent content;
        try
        {
            content = selected == null ? SharePointFileContent.Unsupported() : await selected.ReadFileContentAsync(path, context.MaxFileBytes, context.FileReadTimeout ?? TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            content = new(
                DiscoveryTerminalOutcome.Failed,
                Array.Empty<byte>(),
                false,
                AspxHandlerSourceKind.Unknown,
                AspxHandlerReadState.Unavailable,
                ClassicAspxAcquisitionEvidenceNames.ReadFileContentPrefix + ex.GetType().Name,
                ClassicAspxAcquisitionErrorCodes.FailedFileSourceRead);
        }

        if (content.Outcome != DiscoveryTerminalOutcome.Complete || !content.IsComplete
            || !content.SameAuthorityConfirmed)
        {
            await BufferFileGapAsync(
                path,
                ClassicAspxAcquisitionEvidenceNames.FileSource,
                content.ErrorCode ?? ClassicAspxAcquisitionErrorCodes.UnprovedFileSource,
                content.Outcome,
                cancellationToken,
                evidenceRef: content.EvidenceRef,
                resolverWeb: selected?.WebUrl);
        }

        bool complete = content.IsComplete && content.SameAuthorityConfirmed && content.Bytes != null
            && content.Bytes.Length <= context.MaxFileBytes
            && (content.Outcome == DiscoveryTerminalOutcome.Complete
            || content.Source == AspxHandlerSourceKind.RenderedHtml);
        var readState = content.Outcome == DiscoveryTerminalOutcome.Denied ? AspxHandlerReadState.Denied : content.Outcome == DiscoveryTerminalOutcome.Truncated ? AspxHandlerReadState.Truncated : content.ReadState;
        if (content.Outcome is not DiscoveryTerminalOutcome.Complete
            && content.Source != AspxHandlerSourceKind.RenderedHtml
            && readState == AspxHandlerReadState.Available)
        {
            readState = AspxHandlerReadState.Unavailable;
        }

        var handler = AspxHandlerParser.Parse(
            content.Bytes,
            content.SameAuthorityConfirmed ? content.Source : AspxHandlerSourceKind.Unknown,
            complete,
            readState);
        var result = new ClassicAspxInventory
        {
            ScanId = context.ScanId,
            AspxKey = ClassicAspxInventory.CreateTenantFileKey(context.SiteCollectionId, fileId),
            SiteCollectionId = context.SiteCollectionId,
            FileUniqueId = fileId,
            WebId = owningWeb,
            SiteUrl = context.SiteUrl.AbsoluteUri,
            WebUrl = owningWeb.HasValue ? context.WebUrl.AbsoluteUri : null,
            Url = new Uri(context.SiteUrl, path).AbsoluteUri,
            FileName = file.Name ?? path.Split('/')[^1],
            PageType = observation?.PageType,
            ContentTypeId = observation?.ContentTypeId ?? file.ContentTypeId,
            CustomizedPageStatus = observation?.CustomizedPageStatus ?? file.CustomizedPageStatus,
            SetupPath = observation?.SetupPath,
            FileReadState = file.Outcome.ToString(),
            FileErrorCodes = file.ErrorCode,
            HandlerDeclaredType = handler.DeclaredInherits,
            HandlerNormalizedType = handler.NormalizedInherits,
            HandlerEffectiveType = handler.EffectiveInherits,
            HandlerReadState = handler.ReadState.ToString(),
            HandlerParseState = handler.ParseState.ToString(),
            HandlerSource = handler.Source.ToString(),
            HandlerDefaultProvenance = handler.DefaultProvenance == AspxHandlerDefaultProvenance.None ? null : handler.DefaultProvenance.ToString(),
            HandlerReasonCode = handler.ReasonCode,
            HandlerErrorCodes = content.ErrorCode,
            CoverageState = ClassicAspxCoverageStates.Observed,
            FirstObservedAtUtc = DateTime.UtcNow,
            ObservedAtUtc = DateTime.UtcNow,
            ProvenanceJson = JsonSerializer.Serialize(new[] { new {
                            source,
                            FileEvidence = file.EvidenceRef,
                            ContentEvidence = content.EvidenceRef,
                            path
                        } })

        };
        CacheResource(path, result);
        await BufferInventoryAsync(result, cancellationToken);
        return result;
    }

    // Root is a borrowed, exact same-site-collection client, never a new scanner or inferred owner.
    /// <summary>
    /// Returns the borrowed current-Web client or caches an exact same-site-collection root client for parent-Web
    /// files. Never disposes factory-owned clients or substitutes the file client Web for the Item Web.
    /// </summary>
    private async Task<ISharePointAspxRestClient> GetFileClientForPathAsync(string path, CancellationToken cancellationToken)
    {
        if (!IsPathWithinRoot(path, context.SiteUrl.AbsolutePath))
        {
            await BufferFileGapAsync(
                path,
                ClassicAspxAcquisitionEvidenceNames.FileClientRoute,
                ClassicAspxAcquisitionErrorCodes.OutsideSiteCollection,
                DiscoveryTerminalOutcome.Unknown,
                cancellationToken);
            return null;
        }

        if (IsPathWithinRoot(path, context.WebUrl.AbsolutePath))
        {
            return client;
        }

        if (siteRootClient != null)
        {
            return siteRootClient;
        }

        if (clientFactory == null)
        {
            await BufferFileGapAsync(
                path,
                ClassicAspxAcquisitionEvidenceNames.FileClientRoute,
                ClassicAspxAcquisitionErrorCodes.RootClientUnavailable,
                DiscoveryTerminalOutcome.Unknown,
                cancellationToken);
            return null;
        }

        ISharePointAspxRestClient candidate;
        try
        {
            candidate = await clientFactory.GetAsync(context.SiteUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await BufferFileGapAsync(
                path,
                ClassicAspxAcquisitionEvidenceNames.FileClientRoute,
                ClassicAspxAcquisitionErrorCodes.RootClientFailurePrefix + ex.GetType().Name,
                DiscoveryTerminalOutcome.Failed,
                cancellationToken,
                ex.GetType().FullName);
            return null;
        }

        var root = candidate?.WebUrl;
        if (root == null || !HasSameHttpsAuthority(root, context.SiteUrl) || root.UserInfo.Length != 0
            || root.Query.Length != 0
            || root.Fragment.Length != 0
            || !root.AbsolutePath.TrimEnd('/').Equals(context.SiteUrl.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            await BufferFileGapAsync(
                path,
                ClassicAspxAcquisitionEvidenceNames.FileClientRoute,
                ClassicAspxAcquisitionErrorCodes.RootClientScopeMismatch,
                DiscoveryTerminalOutcome.Unknown,
                cancellationToken);
            return null;
        }

        // Factory owns the root context/client and caches it; this acquisition must not dispose it.
        siteRootClient = candidate;
        return candidate;
    }

    /// <summary>
    /// Records a path-specific, sanitized resolution or source gap so failures for different targets do not
    /// overwrite each other.
    /// </summary>
    private async Task BufferFileGapAsync(
        string path,
        string stage,
        string code,
        DiscoveryTerminalOutcome state,
        CancellationToken cancellationToken,
        string detail = null,
        string evidenceRef = null,
        Uri resolverWeb = null)
    {
        var row = CreateEvidenceRow(
            ClassicAspxAcquisitionEvidenceNames.Gap,
            ClassicAspxAcquisitionEvidenceNames.Acquisition,
            stage + ":" + path + ":" + code,
            state,
            code,
            JsonSerializer.Serialize(new
            {
                ReferencePath = path,
                ResolverWeb = resolverWeb?.AbsoluteUri,
                EvidenceRef = SanitizeFileDetail(evidenceRef),
                ErrorStage = stage,
                ErrorCode = code
            }));
        row.Url = new Uri(context.SiteUrl, path).AbsoluteUri;
        row.ErrorStage = stage;
        row.ErrorDetail = SanitizeFileDetail(detail);
        await BufferEvidenceAsync(row, cancellationToken);
    }

    /// <summary>
    /// Redacts bearer/JWT and secret-like values, removes control characters, and bounds durable diagnostic text
    /// before persistence.
    /// </summary>
    private static string SanitizeFileDetail(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var safe = System.Text.RegularExpressions.Regex.Replace(
            value,
            BearerOrJwtPattern,
            RedactedText,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        safe = System.Text.RegularExpressions.Regex.Replace(
            safe,
            SecretKeyValuePattern,
            RedactedKeyValue,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        safe = new string(safe.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        return safe.Length > MaximumDiagnosticDetailLength ? safe[..MaximumDiagnosticDetailLength] : safe;
    }

    /// <summary>
    /// Stores a normalized path result in the finite per-acquisition cache, retaining the existing full-cache reset
    /// policy.
    /// </summary>
    private void CacheResource(string path, ClassicAspxInventory result)
    {
        if (resources.Count >= context.MaxResourceCacheEntries)
        {
            resources.Clear();
        }

        resources[path] = result;
    }

    /// <summary>
    /// Adds one inventory observation and awaits publication when the combined batch reaches its configured bound.
    /// </summary>
    private async Task BufferInventoryAsync(ClassicAspxInventory row, CancellationToken cancellationToken)
    {
        inventory.Add(row);
        await FlushIfBatchFullAsync(cancellationToken);
    }

    /// <summary>
    /// Adds raw request, scope or gap evidence to the shared bounded batch; persistence errors remain fatal.
    /// </summary>
    private async Task BufferEvidenceAsync(ClassicPageDiscovery row, CancellationToken cancellationToken)
    {
        evidence.Add(row);
        await FlushIfBatchFullAsync(cancellationToken);
    }

    /// <summary>
    /// Buffers an incomplete Web acquisition fact without equating a failure with a proven empty collection.
    /// </summary>
    private Task BufferGapAsync(
        string scope,
        string code,
        DiscoveryTerminalOutcome state,
        CancellationToken cancellationToken) => BufferEvidenceAsync(
        CreateEvidenceRow(
            ClassicAspxAcquisitionEvidenceNames.Gap,
            ClassicAspxAcquisitionEvidenceNames.Acquisition,
            scope + ":" + code,
            state,
            code),
        cancellationToken);
    /// <summary>
    /// Constructs raw evidence independently from normalized inventory, retaining Web scope and stable record-key material.
    /// </summary>
    private ClassicPageDiscovery CreateEvidenceRow(
        string type,
        string scopeType,
        string scope,
        DiscoveryTerminalOutcome state,
        string error = null,
        string json = null) => new()
        {
            ScanId = context.ScanId,
            RecordKey = ClassicAspxAcquisitionEvidenceNames.RecordPrefix + type + ":" + DiscoveryHash.Of(context.WebId.ToString(ServerGuidFormat), scope),
            RowType = type,
            ScopeType = scopeType,
            ParentScopeKey = ClassicAspxAcquisitionEvidenceNames.WebScopePrefix + context.WebId.ToString(ServerGuidFormat),
            SiteUrl = context.SiteUrl.AbsoluteUri,
            WebUrl = SiteRelativeEvidenceWebPath,
            SiteCollectionId = context.SiteCollectionId,
            WebId = context.WebId,
            DiscoveryStatus = state.ToString(),
            ObservationMethod = ClassicAspxAcquisitionEvidenceNames.NativeMethod,
            ErrorStage = error == null ? null : scopeType,
            ErrorCodes = error,
            EvidenceJson = json,
            ObservedAtUtc = DateTime.UtcNow

        };
    /// <summary>
    /// Publishes the buffered arrays once, awaits successful persistence, and only then clears them. A failed
    /// callback leaves the observation lists intact and propagates.
    /// </summary>
    private Task FlushIfBatchFullAsync(CancellationToken cancellationToken)
    {
        return BufferedRowCount >= context.BatchSize
            ? FlushAsync(cancellationToken)
            : Task.CompletedTask;
    }
    /// <summary>
    /// Publishes all buffered observation arrays and clears them only after the persistence callback succeeds.
    /// </summary>
    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (BufferedRowCount == 0)
        {
            return;
        }

        var batch = new M1AcquisitionBatch(inventory.ToArray(), bindings.ToArray(), evidence.ToArray());
        await publish(batch, cancellationToken).ConfigureAwait(false); // Persistence failures must propagate, never become network gaps.
        inventory.Clear();
        bindings.Clear();
        evidence.Clear();
    }

    /// <summary>
    /// Builds a REST endpoint underneath the current Web without replacing its encoded absolute URI.
    /// </summary>
    private Uri CreateEndpointUri(string suffix) => new(context.WebUrl.AbsoluteUri.TrimEnd('/') + "/" + suffix);
    /// <summary>A bounded list-local CT observation; an unloaded display field differs from a loaded empty override.</summary>
    private sealed record ListContentTypeMetadata(
        string Id,
        string DisplayFormUrl,
        bool IsDisplayFormLoaded);
    /// <summary>Pairs the metadata entries with the terminal completeness of their collection authority.</summary>
    private sealed record ContentTypeMetadataSnapshot(IReadOnlyList<ListContentTypeMetadata> Entries, bool IsComplete);
    /// <summary>Server-observed Item values passed together through the independent binding-reason handlers.</summary>
    private sealed class ListItemObservation
    {
        internal JsonElement RawItem { get; init; }
        internal Guid ListId { get; init; }
        internal int ItemId { get; init; }
        internal Guid? ItemGuid { get; init; }
        internal string Title { get; init; }
        internal string ContentTypeId { get; init; }
        internal string ItemUrl { get; init; }
        internal bool OwnsFile { get; init; }
        internal JsonElement File { get; init; }
        internal string FilePath { get; init; }
    }
}
