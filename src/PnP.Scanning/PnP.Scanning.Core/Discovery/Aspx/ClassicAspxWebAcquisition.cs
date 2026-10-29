using PnP.Scanning.Core.Storage;
using System.Text.Json;
using static PnP.Scanning.Core.Discovery.SharePointAspxRestJson;
using static PnP.Scanning.Core.Discovery.SharePointAspxUrlPolicy;

namespace PnP.Scanning.Core.Discovery.Aspx;
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
    private readonly ClassicAspxWebAcquisitionContext context;
    private readonly ISharePointAspxRestClient client;
    private readonly ISharePointAspxRestClientFactory clientFactory;
    private ISharePointAspxRestClient siteRootClient;
    private readonly SharePointAspxUrlPolicy urlPolicy;
    private readonly List<ClassicAspxInventory> inventory = new();
    private readonly List<ClassicPageDiscovery> evidence = new();
    private Func<ClassicAspxAcquisitionBatch, CancellationToken, Task> publish;
    private int BufferedRowCount => inventory.Count + evidence.Count;

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
        ClassicAspxWebAcquisitionContext context,
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
    /// Imports physical ASPX observations. Display-form declarations are captured by Web discovery;
    /// no list items are read to infer page counts or item/resource bindings.
    /// Publishes bounded observation batches; cancellation and persistence failures propagate to the native worker.
    /// </summary>
    internal async Task AcquireAsync(
        IAsyncEnumerable<ClassicPageDiscovery> physicalPages,
        Func<ClassicAspxAcquisitionBatch, CancellationToken, Task> publish,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(physicalPages);
        this.publish = publish ?? throw new ArgumentNullException(nameof(publish));
        await ImportPhysicalObservationsAsync(physicalPages, cancellationToken);
        await BufferEvidenceAsync(CreateEvidenceRow(
            ClassicAspxAcquisitionEvidenceNames.Scope,
            ClassicAspxAcquisitionScopes.ItemEnumerationPolicy,
            ClassicAspxAcquisitionScopes.ListItemsNotEnumerated,
            DiscoveryTerminalOutcome.PolicyExcluded,
            json: "{\"itemPages\":\"NotCollected\",\"itemBindings\":\"NotCollected\",\"displayForms\":\"MetadataDeclarationsOnly\"}"),
            cancellationToken);
        await FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Records real physical identities without treating a scheduling WebId or renderer pointer as
    /// backing-item proof.
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
        }
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

        var batch = new ClassicAspxAcquisitionBatch(inventory.ToArray(), Array.Empty<ClassicAspxItemBinding>(), evidence.ToArray());
        await publish(batch, cancellationToken).ConfigureAwait(false); // Persistence failures must propagate, never become network gaps.
        inventory.Clear();
        evidence.Clear();
    }

}
