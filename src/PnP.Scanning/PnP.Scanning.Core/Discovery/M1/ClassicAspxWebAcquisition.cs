using PnP.Scanning.Core.Storage;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Net;

namespace PnP.Scanning.Core.Discovery.M1;

/// <summary>Incremental native acquisition only. Persistence/union and scan scheduling belong to the caller.</summary>
internal sealed class ClassicAspxWebAcquisition
{
    private readonly M1WebAcquisitionContext context;
    private readonly ISharePointAspxRestClient client;
    private readonly ISharePointAspxRestClientFactory clientFactory;
    private ISharePointAspxRestClient siteRootClient;
    private readonly List<ClassicAspxInventory> inventory = new();
    private readonly List<ClassicAspxItemBinding> bindings = new();
    private readonly List<ClassicPageDiscovery> evidence = new();
    private readonly Dictionary<string, ClassicAspxInventory> resources = new(StringComparer.OrdinalIgnoreCase);
    private Func<M1AcquisitionBatch, CancellationToken, Task> publish;
    private int size => inventory.Count + bindings.Count + evidence.Count;
    private string LegacyWeb => context.WebUrl.AbsolutePath.TrimEnd('/')[context.SiteUrl.AbsolutePath.TrimEnd('/').Length..] is { Length: > 0 } relative ? relative : "/";

    internal ClassicAspxWebAcquisition(M1WebAcquisitionContext context, ISharePointAspxRestClient client,
        ISharePointAspxRestClientFactory clientFactory = null)
    {
        this.context = context ?? throw new ArgumentNullException(nameof(context));
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.clientFactory = clientFactory;
        SharePointFileContentReader.ValidateWeb(context.SiteUrl);
        SharePointFileContentReader.ValidateWeb(context.WebUrl);
        SharePointFileContentReader.ValidateWeb(client.WebUrl);
        if (context.ScanId == Guid.Empty || context.SiteCollectionId == Guid.Empty || context.WebId == Guid.Empty ||
            !SameAuthority(context.SiteUrl, context.WebUrl) || !SameAuthority(context.WebUrl, client.WebUrl) ||
            context.WebUrl.AbsolutePath.TrimEnd('/') != client.WebUrl.AbsolutePath.TrimEnd('/') ||
            !Within(context.WebUrl.AbsolutePath, context.SiteUrl.AbsolutePath) ||
            context.BatchSize is < 1 or > 200 || context.MaxCollectionPages < 1 || context.MaxContentTypes < 1 ||
            context.MaxResourceCacheEntries < 1 || context.MaxFileBytes is < 1 or > 16 * 1024 * 1024)
            throw new ArgumentException("Exact server IDs, web/client scope and bounded acquisition limits are required.");
    }

    internal async Task AcquireAsync(IAsyncEnumerable<ClassicPageDiscovery> physicalPages,
        Func<M1AcquisitionBatch, CancellationToken, Task> publish, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(physicalPages);
        this.publish = publish ?? throw new ArgumentNullException(nameof(publish));
        await foreach (var row in physicalPages.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (row.RowType != "Page") continue;
            if (row.ScanId != context.ScanId || row.SiteCollectionId != context.SiteCollectionId ||
                !Good(row.FileUniqueId) || !TryPath(row.Url, out var path) || !Aspx(path))
            {
                await Gap("Physical", "physical_identity_or_scope_unproved", DiscoveryTerminalOutcome.Unknown, cancellationToken);
                continue;
            }
            // Legacy ListItem observations point to a renderer, not to that item's own File.
            var file = new SharePointResolvedFile(DiscoveryTerminalOutcome.Complete, row.FileUniqueId.Value.ToString("D"),
                row.FileName, path, row.CustomizedPageStatus, row.ObservationMethod ?? "PhysicalSourceObservation",
                ListId: row.ListId, ListItemId: row.ListItemId, ContentTypeId: row.ContentTypeId);
            await ObserveFile(file, path, "PhysicalFileObservation", row, cancellationToken);
            // A legacy scheduling WebId does not prove the physical file or backing item owner.
            // Full acquisition confirms FileSelf through this web's actual Item.File API below.
            if (context.Options?.HomePageOnly == true && Good(row.ListId) && row.ListItemId > 0)
                await Gap("HomePageOnly", "homepage_backing_requires_targeted_item_confirmation", DiscoveryTerminalOutcome.Unknown, cancellationToken);
        }

        if (context.Options?.HomePageOnly == true)
        {
            await Evidence(Row("Scope", "Surface", "Items:HomePageOnly", DiscoveryTerminalOutcome.PolicyExcluded,
                "explicit_homepage_scope_does_not_authorize_all_items"), cancellationToken);
            await Flush(cancellationToken);
            return;
        }

        const string listSelect = "Id,Title,BaseType,BaseTemplate,Hidden,IsCatalog,DefaultDisplayFormUrl,RootFolder/ServerRelativeUrl";
        await foreach (var list in ReadPages("Lists", Endpoint("_api/web/lists?$select=" + listSelect + "&$expand=RootFolder&$top=200"), cancellationToken))
        {
            var listId = GuidValue(list, "Id");
            if (!Good(listId)) { await Gap("Lists", "list_guid_missing", DiscoveryTerminalOutcome.Unknown, cancellationToken); continue; }
            var types = new List<ContentType>();
            bool ctComplete = false;
            string ctScope = "ContentTypes:" + listId.Value.ToString("D");
            await foreach (var type in ReadPages(ctScope, Endpoint($"_api/web/lists(guid'{listId:D}')/ContentTypes?$select=Id,DisplayFormUrl&$top=200"), cancellationToken,
                done => ctComplete = done))
            {
                if (types.Count >= context.MaxContentTypes)
                {
                    await Gap(ctScope, "content_type_metadata_limit", DiscoveryTerminalOutcome.Truncated, cancellationToken);
                    ctComplete = false;
                    break;
                }
                string id = ContentTypeId(type, "Id");
                if (string.IsNullOrWhiteSpace(id)) { ctComplete = false; types.Add(new(null, null, false)); }
                else types.Add(new(id, Text(type, "DisplayFormUrl"), Property(type, "DisplayFormUrl", out _)));
            }
            if (types.Any(t => t.Id == null) || types.GroupBy(t => t.Id, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            {
                ctComplete = false;
                await Gap(ctScope, "content_type_identity_ambiguous", DiscoveryTerminalOutcome.Unknown, cancellationToken);
            }
            // '*' avoids asking generic lists for optional Publishing fields they do not have.
            // No Hidden/BaseType/content-type-family filter is applied to the business item authority.
            string itemScope = "Items:" + listId.Value.ToString("D");
            await foreach (var item in ReadPages(itemScope,
                Endpoint($"_api/web/lists(guid'{listId:D}')/items?$select=*,File/UniqueId,File/Name,File/ServerRelativeUrl&$expand=File&$top=200"), cancellationToken))
            {
                int? id = Integer(item, "Id");
                if (id is null or <= 0) { await Gap(itemScope, "item_positive_id_missing", DiscoveryTerminalOutcome.Unknown, cancellationToken); continue; }
                Guid? itemGuid = GuidValue(item, "GUID");
                string title = Text(item, "Title"), ct = ContentTypeId(item, "ContentTypeId"), itemUrl = Text(item, "FileRef");
                bool ownsFile = Property(item, "File", out var ownFile) && Good(GuidValue(ownFile, "UniqueId"));
                string ownPath = ownsFile ? Text(ownFile, "ServerRelativeUrl") : null;
                if (ownsFile && TryPath(ownPath, out var physical) && Aspx(physical))
                {
                    var found = new SharePointResolvedFile(DiscoveryTerminalOutcome.Complete, GuidValue(ownFile, "UniqueId").Value.ToString("D"),
                        Text(ownFile, "Name"), physical, null, "REST:Item.File expansion", ListId: listId, ListItemId: id, ContentTypeId: ct);
                    var resource = await ObserveFile(found, physical, "Item.File", null, cancellationToken, context.WebId);
                    if (resource != null) await Bind(resource, listId.Value, id.Value, itemGuid, title, itemUrl, ct,
                        ClassicAspxBindingKinds.FileSelf, "Item.File expansion", cancellationToken);
                }
                else if (!ownsFile && TryPath(itemUrl, out var filePath) && Aspx(filePath))
                {
                    var file = await Resolve(filePath, cancellationToken);
                    if (file != null && file.ListId == listId && file.ListItemId == id)
                    {
                        var resource = await ObserveFile(file, filePath, "ResolvedItemFileBacking", null, cancellationToken);
                        if (resource != null) await Bind(resource, listId.Value, id.Value, itemGuid, title, itemUrl, ct,
                            ClassicAspxBindingKinds.FileSelf, "ResolvedFile exact ListId/ItemId", cancellationToken);
                    }
                }

                if (ctComplete)
                {
                    var matching = types.Where(t => t.Id != null && ct != null && ct.StartsWith(t.Id, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(t => t.Id.Length).ToArray();
                    if (matching.Length == 0 || !matching[0].DisplayKnown)
                        await Gap(itemScope, "item_content_type_renderer_unproved", DiscoveryTerminalOutcome.Unknown, cancellationToken);
                    else
                    {
                        var selected = matching[0]; // Empty most-specific override falls to list, never an ancestor override.
                        bool overridden = !string.IsNullOrWhiteSpace(selected.DisplayForm);
                        string renderer = overridden ? selected.DisplayForm : Text(list, "DefaultDisplayFormUrl");
                        if (!overridden && !Property(list, "DefaultDisplayFormUrl", out _))
                            await Gap(itemScope, "list_default_renderer_unloaded", DiscoveryTerminalOutcome.Unknown, cancellationToken);
                        else if (!string.IsNullOrWhiteSpace(renderer))
                        {
                            var resource = await Resource(renderer, overridden ? "ContentType.DisplayFormUrl" : "List.DefaultDisplayFormUrl", cancellationToken);
                            var kind = overridden ? ClassicAspxBindingKinds.ContentTypeRenderer : ClassicAspxBindingKinds.ListFormRenderer;
                            if (ownsFile && !Aspx(ownPath)) kind |= ClassicAspxBindingKinds.DocumentMetadataRenderer;
                            if (resource != null) await Bind(resource, listId.Value, id.Value, itemGuid, title, itemUrl, ct, kind,
                                overridden ? "MostSpecificListContentType.DisplayFormUrl" : "List.DefaultDisplayFormUrl", cancellationToken);
                        }
                    }
                }
                else await Gap(itemScope, "content_type_scope_incomplete_no_renderer_fallback", DiscoveryTerminalOutcome.Unknown, cancellationToken);

                string layout = UrlField(item, "PublishingPageLayout");
                if (!string.IsNullOrWhiteSpace(layout))
                {
                    var resource = await Resource(layout, "PublishingPageLayout", cancellationToken);
                    if (resource != null) await Bind(resource, listId.Value, id.Value, itemGuid, title, itemUrl, ct,
                        ClassicAspxBindingKinds.PublishingPageLayout, "Item.PublishingPageLayout", cancellationToken);
                }
            }
        }
        await Flush(cancellationToken);
    }

    private async IAsyncEnumerable<JsonElement> ReadPages(string scope, Uri first,
        [EnumeratorCancellation] CancellationToken ct, Action<bool> completed = null)
    {
        var receipts = new List<AspxPaginationPageReceipt>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { first.AbsoluteUri };
        Uri next = first;
        string token = null;
        int observed = 0;
        var outcome = DiscoveryTerminalOutcome.Unknown;
        for (int ordinal = 0; next != null && ordinal < context.MaxCollectionPages; ordinal++)
        {
            SharePointRestPage page;
            try { page = await client.GetPageAsync(next, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                page = new(next, 0, Array.Empty<JsonElement>(), null, DiscoveryHash.Of(ex.GetType().FullName),
                    "transport-error", DiscoveryTerminalOutcome.Failed, "transport_failure", ReceivedAtUtc: DateTimeOffset.UtcNow);
            }
            bool uriValid = page.RequestUri != null && page.RequestUri.AbsoluteUri == next.AbsoluteUri;
            outcome = uriValid ? page.Outcome : DiscoveryTerminalOutcome.Unknown;
            bool terminal = string.IsNullOrWhiteSpace(page.NextLink);
            var receipt = new AspxPaginationPageReceipt(AspxAcquisitionVersions.PaginationReceipt, scope,
                context.AuthorityRevision, DiscoveryHash.Of(first.GetLeftPart(UriPartial.Path), first.Query), "GET",
                AspxDurableRequestEvidence.Endpoint(page.RequestUri ?? next), first.Query, string.Empty, ordinal,
                AspxPaginationContract.TokenHash(token), page.Items.Count, AspxPaginationContract.TokenHash(page.NextLink),
                page.ResponseDigest, page.StatusCode == 0 ? null : (int)page.StatusCode, page.SemanticDetectorResult,
                page.AttemptCount, page.AttemptLimit, page.RequestId, page.CorrelationId,
                uriValid ? page.ErrorCode : "pagination_response_uri_drift", terminal,
                page.ReceivedAtUtc == default ? DateTimeOffset.UtcNow : page.ReceivedAtUtc);
            receipts.Add(receipt);
            await Evidence(Row("Pagination", "RequestPage", scope + ":" + ordinal, outcome,
                receipt.ErrorCode, JsonSerializer.Serialize(receipt)), ct);
            if (outcome != DiscoveryTerminalOutcome.Complete) break;
            foreach (var row in page.Items) { observed++; yield return row; }
            if (terminal) { next = null; break; }
            if (!TryNext(first, page.NextLink, out var resolved) || !seen.Add(resolved.AbsoluteUri))
            {
                outcome = DiscoveryTerminalOutcome.Unknown;
                await Gap(scope, "pagination_unsafe_or_repeated_cursor", outcome, ct);
                break;
            }
            token = page.NextLink;
            next = resolved;
        }
        if (next != null && outcome == DiscoveryTerminalOutcome.Complete) outcome = DiscoveryTerminalOutcome.Truncated;
        var validation = AspxPaginationContract.Validate(receipts, outcome);
        bool success = next == null && validation.Outcome is DiscoveryTerminalOutcome.Complete or DiscoveryTerminalOutcome.Empty;
        completed?.Invoke(success);
        var state = success && observed == 0 ? DiscoveryTerminalOutcome.Empty : success ? DiscoveryTerminalOutcome.Complete : outcome;
        if (!success && state is DiscoveryTerminalOutcome.Complete or DiscoveryTerminalOutcome.Empty) state = DiscoveryTerminalOutcome.Unknown;
        await Evidence(Row("Scope", "Surface", scope, state, success ? null : "m1_collection_incomplete",
            JsonSerializer.Serialize(new
            {
                observed,
                validation.ChainHash,
                validation.OutstandingTokenCount,
                validation.GapCodes,
                ExpectedCount = success ? (int?)observed : null,
                TerminalComplete = success
            })), ct);
    }

    private async Task<ClassicAspxInventory> Resource(string reference, string source, CancellationToken ct)
    {
        if (!TryPath(reference, out var path) || !Aspx(path))
        { await Gap(source, "renderer_path_unsupported", DiscoveryTerminalOutcome.Unknown, ct); return null; }
        if (resources.TryGetValue(path, out var cached)) return cached;
        if (path.StartsWith("/_layouts/", StringComparison.OrdinalIgnoreCase))
        {
            IReadOnlyList<PlatformAspxCatalogEntry> entries = Array.Empty<PlatformAspxCatalogEntry>();
            try { if (context.Catalog != null) entries = context.Catalog.ResolvePath(path); }
            catch (ArgumentException) { }
            if (entries.Count != 1)
            { await Gap(source, entries.Count == 0 ? "platform_catalog_member_unresolved" : "platform_catalog_alias_ambiguous", DiscoveryTerminalOutcome.Unknown, ct); return null; }
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
                FileReadState = "NotApplicable",
                HandlerReadState = entry.DeclaredInherits == null ? "NotRequested" : "Available",
                HandlerParseState = entry.DeclaredInherits == null ? "NotParsed" : "Declared",
                HandlerSource = "VersionedCatalogReference",
                HandlerDeclaredType = entry.DeclaredInherits,
                HandlerNormalizedType = entry.DeclaredInherits?.Trim(),
                HandlerEffectiveType = entry.DeclaredInherits?.Trim(),
                CoverageState = "VersionedReference",
                FirstObservedAtUtc = DateTime.UtcNow,
                ObservedAtUtc = DateTime.UtcNow,
                ProvenanceJson = JsonSerializer.Serialize(new[] { new { source, path, entry.HandlerEvidenceSource } })
            };
            Cache(path, result); await Inventory(result, ct); return result;
        }
        var file = await Resolve(path, ct);
        return file == null ? null : await ObserveFile(file, path, source, null, ct);
    }

    private async Task<SharePointResolvedFile> Resolve(string path, CancellationToken ct)
    {
        var selected = await FileClient(path, ct);
        if (selected == null) return null;
        SharePointResolvedFile file;
        try { file = await selected.ResolveFileAsync(path, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            await FileGap(path, "FileResolution", "file_resolution_" + ex.GetType().Name, DiscoveryTerminalOutcome.Failed, ct,
            ex.GetType().FullName, resolverWeb: selected.WebUrl); return null;
        }
        if (file.Outcome != DiscoveryTerminalOutcome.Complete || !Guid.TryParse(file.FileUniqueId, out var id) || id == Guid.Empty ||
            !TryPath(file.ServerRelativeUrl, out var actual) || !string.Equals(path, actual, StringComparison.OrdinalIgnoreCase))
        {
            await FileGap(path, file.ErrorStage ?? "FileResolution", file.ErrorCode ?? "file_identity_or_alias_unproved",
                file.Outcome == DiscoveryTerminalOutcome.Denied ? file.Outcome : DiscoveryTerminalOutcome.Unknown, ct,
                file.ErrorDetail, file.EvidenceRef, selected.WebUrl);
            return null;
        }
        return file;
    }

    private async Task<ClassicAspxInventory> ObserveFile(SharePointResolvedFile file, string path, string source,
        ClassicPageDiscovery observation, CancellationToken ct, Guid? owningWeb = null)
    {
        if (!Guid.TryParse(file.FileUniqueId, out var fileId) || fileId == Guid.Empty) return null;
        var selected = await FileClient(path, ct);
        SharePointFileContent content;
        try
        {
            content = selected == null ? SharePointFileContent.Unsupported() :
            await selected.ReadFileContentAsync(path, context.MaxFileBytes, context.FileReadTimeout ?? TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            content = new(DiscoveryTerminalOutcome.Failed, Array.Empty<byte>(), false, AspxHandlerSourceKind.Unknown,
            AspxHandlerReadState.Unavailable, "ReadFileContentAsync:" + ex.GetType().Name, "file_source_read_failed");
        }
        if (content.Outcome != DiscoveryTerminalOutcome.Complete || !content.IsComplete || !content.SameAuthorityConfirmed)
            await FileGap(path, "FileSource", content.ErrorCode ?? "file_source_unproved", content.Outcome, ct,
                evidenceRef: content.EvidenceRef, resolverWeb: selected?.WebUrl);
        bool complete = content.IsComplete && content.SameAuthorityConfirmed && content.Bytes != null && content.Bytes.Length <= context.MaxFileBytes &&
            (content.Outcome == DiscoveryTerminalOutcome.Complete || content.Source == AspxHandlerSourceKind.RenderedHtml);
        var readState = content.Outcome == DiscoveryTerminalOutcome.Denied ? AspxHandlerReadState.Denied :
            content.Outcome == DiscoveryTerminalOutcome.Truncated ? AspxHandlerReadState.Truncated : content.ReadState;
        if (content.Outcome is not DiscoveryTerminalOutcome.Complete && content.Source != AspxHandlerSourceKind.RenderedHtml && readState == AspxHandlerReadState.Available)
            readState = AspxHandlerReadState.Unavailable;
        var handler = AspxHandlerParser.Parse(content.Bytes, content.SameAuthorityConfirmed ? content.Source : AspxHandlerSourceKind.Unknown,
            complete, readState);
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
            CoverageState = "Observed",
            FirstObservedAtUtc = DateTime.UtcNow,
            ObservedAtUtc = DateTime.UtcNow,
            ProvenanceJson = JsonSerializer.Serialize(new[] { new { source, FileEvidence = file.EvidenceRef, ContentEvidence = content.EvidenceRef, path } })
        };
        Cache(path, result); await Inventory(result, ct); return result;
    }

    // Root is a borrowed, exact same-site-collection client, never a new scanner or inferred owner.
    private async Task<ISharePointAspxRestClient> FileClient(string path, CancellationToken ct)
    {
        if (!Within(path, context.SiteUrl.AbsolutePath))
        {
            await FileGap(path, "FileClientRoute", "reference_outside_site_collection", DiscoveryTerminalOutcome.Unknown, ct);
            return null;
        }
        if (Within(path, context.WebUrl.AbsolutePath)) return client;
        if (siteRootClient != null) return siteRootClient;
        if (clientFactory == null)
        {
            await FileGap(path, "FileClientRoute", "same_site_root_client_unavailable", DiscoveryTerminalOutcome.Unknown, ct);
            return null;
        }
        ISharePointAspxRestClient candidate;
        try { candidate = await clientFactory.GetAsync(context.SiteUrl, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            await FileGap(path, "FileClientRoute", "same_site_root_client_" + ex.GetType().Name, DiscoveryTerminalOutcome.Failed, ct,
                ex.GetType().FullName);
            return null;
        }
        var root = candidate?.WebUrl;
        if (root == null || !SameAuthority(root, context.SiteUrl) || root.UserInfo.Length != 0 || root.Query.Length != 0 || root.Fragment.Length != 0 ||
            !root.AbsolutePath.TrimEnd('/').Equals(context.SiteUrl.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            await FileGap(path, "FileClientRoute", "same_site_root_client_scope_mismatch", DiscoveryTerminalOutcome.Unknown, ct);
            return null;
        }
        // Factory owns the root context/client and caches it; this acquisition must not dispose it.
        siteRootClient = candidate;
        return candidate;
    }

    private async Task FileGap(string path, string stage, string code, DiscoveryTerminalOutcome state, CancellationToken ct,
        string detail = null, string evidenceRef = null, Uri resolverWeb = null)
    {
        var row = Row("Gap", "M1Acquisition", stage + ":" + path + ":" + code, state, code,
            JsonSerializer.Serialize(new
            {
                ReferencePath = path,
                ResolverWeb = resolverWeb?.AbsoluteUri,
                EvidenceRef = SafeFileDetail(evidenceRef),
                ErrorStage = stage,
                ErrorCode = code
            }));
        row.Url = new Uri(context.SiteUrl, path).AbsoluteUri;
        row.ErrorStage = stage;
        row.ErrorDetail = SafeFileDetail(detail);
        await Evidence(row, ct);
    }

    private static string SafeFileDetail(string value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var safe = System.Text.RegularExpressions.Regex.Replace(value, @"\bBearer\s+\S+|\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b", "[redacted]",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        safe = System.Text.RegularExpressions.Regex.Replace(safe, @"(access_token|refresh_token|client_secret|password)\s*[:=]\s*[^&\s;]+", "$1=[redacted]",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        safe = new string(safe.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        return safe.Length > 1024 ? safe[..1024] : safe;
    }

    private void Cache(string path, ClassicAspxInventory result)
    {
        if (resources.Count >= context.MaxResourceCacheEntries) resources.Clear();
        resources[path] = result;
    }

    private async Task Bind(ClassicAspxInventory resource, Guid list, int item, Guid? itemGuid, string title,
        string itemUrl, string contentType, ClassicAspxBindingKinds kind, string source, CancellationToken ct)
    {
        bindings.Add(new ClassicAspxItemBinding
        {
            ScanId = context.ScanId,
            AspxKey = resource.AspxKey,
            SiteCollectionId = context.SiteCollectionId,
            WebId = context.WebId,
            ListId = list,
            ItemId = item,
            ItemGuid = Good(itemGuid) ? itemGuid : null,
            ItemGuidReadState = Good(itemGuid) ? "Available" : "Unavailable",
            SiteUrl = context.SiteUrl.AbsoluteUri,
            WebUrl = context.WebUrl.AbsoluteUri,
            ItemUrl = itemUrl,
            ItemTitle = title,
            ContentTypeId = contentType,
            BindingKinds = kind,
            ObservedAtUtc = DateTime.UtcNow,
            BindingProvenanceJson = JsonSerializer.Serialize(new[] { new { source, realItemId = item } })
        });
        await MaybeFlush(ct);
    }
    private async Task Inventory(ClassicAspxInventory row, CancellationToken ct) { inventory.Add(row); await MaybeFlush(ct); }
    private async Task Evidence(ClassicPageDiscovery row, CancellationToken ct) { evidence.Add(row); await MaybeFlush(ct); }
    private Task Gap(string scope, string code, DiscoveryTerminalOutcome state, CancellationToken ct) => Evidence(Row("Gap", "M1Acquisition", scope + ":" + code, state, code), ct);
    private ClassicPageDiscovery Row(string type, string scopeType, string scope, DiscoveryTerminalOutcome state, string error = null, string json = null) => new()
    {
        ScanId = context.ScanId,
        RecordKey = "m1:" + type + ":" + DiscoveryHash.Of(context.WebId.ToString("D"), scope),
        RowType = type,
        ScopeType = scopeType,
        ParentScopeKey = "m1:web:" + context.WebId.ToString("D"),
        SiteUrl = context.SiteUrl.AbsoluteUri,
        WebUrl = LegacyWeb,
        SiteCollectionId = context.SiteCollectionId,
        WebId = context.WebId,
        DiscoveryStatus = state.ToString(),
        ObservationMethod = "NativeM1Acquisition",
        ErrorStage = error == null ? null : scopeType,
        ErrorCodes = error,
        EvidenceJson = json,
        ObservedAtUtc = DateTime.UtcNow
    };
    private Task MaybeFlush(CancellationToken ct) => size >= context.BatchSize ? Flush(ct) : Task.CompletedTask;
    private async Task Flush(CancellationToken ct)
    {
        if (size == 0) return;
        var batch = new M1AcquisitionBatch(inventory.ToArray(), bindings.ToArray(), evidence.ToArray());
        await publish(batch, ct).ConfigureAwait(false); // Persistence failures must propagate, never become network gaps.
        inventory.Clear(); bindings.Clear(); evidence.Clear();
    }

    private Uri Endpoint(string suffix) => new(context.WebUrl.AbsoluteUri.TrimEnd('/') + "/" + suffix);
    private bool TryPath(string value, out string path)
    {
        path = null;
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\\') || value.Contains("%2f", StringComparison.OrdinalIgnoreCase) || value.Contains("%5c", StringComparison.OrdinalIgnoreCase)) return false;
        string rawPath = value.Split('?', '#')[0];
        if (rawPath.Split('/').Any(s => s is "." or "..")) return false;
        if (!Uri.TryCreate(new Uri(context.WebUrl.AbsoluteUri.TrimEnd('/') + "/"), value, out var uri) || !SameAuthority(uri, context.SiteUrl) || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        string decoded = Uri.UnescapeDataString(uri.AbsolutePath);
        if (decoded.Any(char.IsControl) || decoded.Split('/').Any(s => s is "." or "..") || decoded.Contains("%2f", StringComparison.OrdinalIgnoreCase) || decoded.Contains("%5c", StringComparison.OrdinalIgnoreCase)) return false;
        if (!decoded.StartsWith("/_layouts/", StringComparison.OrdinalIgnoreCase) && !Within(uri.AbsolutePath, context.SiteUrl.AbsolutePath)) return false;
        path = uri.AbsolutePath;
        return true;
    }
    private static bool TryNext(Uri first, string raw, out Uri next)
    {
        next = null;
        if (!Uri.TryCreate(first, raw, out var candidate) || !SameAuthority(first, candidate) ||
            !string.IsNullOrEmpty(candidate.UserInfo) || !string.IsNullOrEmpty(candidate.Fragment) ||
            !string.Equals(first.AbsolutePath, candidate.AbsolutePath, StringComparison.OrdinalIgnoreCase)) return false;
        var expected = Query(first); var actual = Query(candidate);
        if (actual == null || expected == null || actual.TryGetValue("$filter", out var filter) && !string.IsNullOrWhiteSpace(filter)) return false;
        foreach (var pair in expected) if (!actual.TryGetValue(pair.Key, out var value) || value != pair.Value) return false;
        next = candidate; return true;
    }
    private static Dictionary<string, string> Query(Uri uri)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equal = part.IndexOf('=');
            string key = WebUtility.UrlDecode(equal < 0 ? part : part[..equal]);
            if (!result.TryAdd(key, WebUtility.UrlDecode(equal < 0 ? "" : part[(equal + 1)..]))) return null;
        }
        return result;
    }
    private static bool SameAuthority(Uri a, Uri b) => a != null && b != null && a.Scheme == "https" && b.Scheme == "https" && a.IsDefaultPort && b.IsDefaultPort && string.Equals(a.Authority, b.Authority, StringComparison.OrdinalIgnoreCase);
    private static bool Within(string path, string root) => root.TrimEnd('/') == "" || path.TrimEnd('/').Equals(root.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) || path.StartsWith(root.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);
    private static bool Aspx(string path) => path != null && path.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase);
    private static bool Good(Guid? id) => id.HasValue && id.Value != Guid.Empty;
    private static bool Property(JsonElement row, string name, out JsonElement value) => PnPContextSharePointAspxRestClient.TryProperty(row, name, out value);
    private static string Text(JsonElement row, string name) => PnPContextSharePointAspxRestClient.String(row, name);
    private static int? Integer(JsonElement row, string name) => int.TryParse(Text(row, name), out int value) ? value : null;
    private static Guid? GuidValue(JsonElement row, string name) => Guid.TryParse(Text(row, name), out var id) && id != Guid.Empty ? id : null;
    private static string ContentTypeId(JsonElement row, string name) => Property(row, name, out var value) && value.ValueKind == JsonValueKind.Object ? Text(value, "StringValue") : Text(row, name);
    private static string UrlField(JsonElement row, string name) => Property(row, name, out var value) && value.ValueKind == JsonValueKind.Object ? Text(value, "Url") : Text(row, name);
    private sealed record ContentType(string Id, string DisplayForm, bool DisplayKnown);
}
