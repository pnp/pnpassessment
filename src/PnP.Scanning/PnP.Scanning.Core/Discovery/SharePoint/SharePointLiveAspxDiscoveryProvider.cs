using PnP.Core.Model.SharePoint;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace PnP.Scanning.Core.Discovery;

/// <summary>
/// Authentication/context-backed live SharePoint acquisition provider. All authority collections are
/// requested without BaseType, Hidden, catalog, template or page-family admission filters.
/// </summary>
internal sealed class SharePointLiveAspxDiscoveryProvider : IAspxDiscoveryProvider, IAspxReferenceAcquisitionProvider
{
    // Display-form declarations ride along on the all-lists request. They are evidence about
    // possible rendering routes, not evidence that any particular list item is a page.
    private const string AllListsSelect = "Id,Title,BaseType,BaseTemplate,Hidden,IsCatalog,RootFolder/ServerRelativeUrl,RootFolder/UniqueId,DefaultViewUrl,DefaultDisplayFormUrl,ContentTypes/Id,ContentTypes/DisplayFormUrl";
    private const string AllListsExpand = "RootFolder,ContentTypes";
    private const string FormsSelect = "Id,ServerRelativeUrl,FormType";
    private const string ViewsSelect = "Id,ServerRelativeUrl,Hidden,DefaultView,PersonalView,Title";
    private const string FilesSelect = "UniqueId,Name,ServerRelativeUrl,CustomizedPageStatus,ListItemAllFields/Id,ListItemAllFields/ContentTypeId,Length,Properties";
    private const string FilesExpand = "ListItemAllFields,Properties";
    // REST encodes the '_' of a property bag name as '_x005f_', so vti_setuppath arrives under this name.
    private const string RestSetupPathProperty = "vti_x005f_setuppath";
    private const string FoldersSelect = "UniqueId,Name,ServerRelativeUrl";
    private readonly SharePointLiveAspxDiscoveryOptions options;
    private readonly ISharePointAspxRestClientFactory clientFactory;
    private readonly SharePointListMetadataSnapshot sharedLists;
    private readonly Dictionary<string, LiveScope> scopes = new(StringComparer.Ordinal);
    private const string MissingFileIdentity = "file_unique_id_missing_or_invalid";
    private const string PhysicalListsSelect = "Id,Title,BaseType,BaseTemplate,Hidden,IsCatalog,RootFolder/ServerRelativeUrl,RootFolder/UniqueId";
    private readonly Dictionary<string, SharePointModeledFolderResult> modeledFolders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SharePointModeledFolderResult> completeModeledFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LiveCollectionResult> folderCollections = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> emittedFileScopes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> invalidFileIdentityScopes = new(StringComparer.Ordinal);
    private readonly HashSet<string> invalidFolderScopes = new(StringComparer.Ordinal);
    private readonly List<LiveScope> listRoots = new();
    private readonly Dictionary<string, string> observedFolderIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RawDiscoveryRecord> targetedFiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> welcomePageByWeb = new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;

    /// <summary>Binds one authorized Web, shared list snapshot and provider-local physical-folder caches.</summary>
    internal SharePointLiveAspxDiscoveryProvider(SharePointLiveAspxDiscoveryOptions options,
        ISharePointAspxRestClientFactory clientFactory, AspxWebAcquisitionContext web,
        SharePointListMetadataSnapshot sharedLists = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        ArgumentNullException.ThrowIfNull(web);
        if (!web.SiteUrl.IsAbsoluteUri || web.SiteUrl.Scheme != Uri.UriSchemeHttps ||
            !web.WebUrl.IsAbsoluteUri || web.WebUrl.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Site and Web URLs must be absolute HTTPS URLs.", nameof(web));
        // A snapshot never authorizes reuse for another Web, including a descendant or sibling.
        this.sharedLists = sharedLists != null && sharedLists.SupportsFields(AllListsSelect) &&
            string.Equals(sharedLists.WebUrl.GetLeftPart(UriPartial.Authority),
                web.WebUrl.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(sharedLists.WebUrl.AbsolutePath.TrimEnd('/'), web.WebUrl.AbsolutePath.TrimEnd('/'),
                StringComparison.Ordinal) && string.IsNullOrEmpty(web.WebUrl.Query) &&
            string.IsNullOrEmpty(web.WebUrl.Fragment) ? sharedLists : null;
        var root = new LiveScope(Key("web", web.WebUrl.AbsoluteUri), null, DiscoveryScopeKind.Web, null,
            web.WebUrl.AbsoluteUri.TrimEnd('/'), "web", web.WebUrl, null, null, null,
            new Dictionary<string, string>
            {
                ["siteCollectionId"] = web.SiteCollectionId.ToString("D"),
                ["siteUrl"] = web.SiteUrl.AbsoluteUri,
                ["webId"] = web.WebId.ToString("D"),
                ["webTemplateConfiguration"] = web.WebTemplateConfiguration ?? string.Empty,
            });
        scopes.Add(root.ScopeKey, root);
        RootScope = Registration(root);
    }

    public DiscoveryScopeRegistration RootScope { get; }
    public AspxReferenceCollector ReferenceCollector { get; } = new();

    public Task<DiscoveryChildEnumerationResult> EnumerateChildrenAsync(DiscoveryScopeRegistration parent,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var scope = scopes[parent.ScopeKey];
        return scope.Kind switch
        {
            DiscoveryScopeKind.Web => EnumerateWebAsync(scope, cancellationToken),
            DiscoveryScopeKind.Container => EnumerateContainerAsync(scope, cancellationToken),
            DiscoveryScopeKind.Folder => EnumerateFolderAsync(scope, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(parent), parent.Kind, null),
        };
    }

    /// <summary>
    /// Creates deferred readers for physical folder files, form/view references, or the targeted welcome file; structural scopes have no source.
    /// Rejects disposed providers and unsupported source roles rather than silently returning empty observations.
    /// </summary>
    public IRawDiscoverySource CreateRawSource(DiscoveryScopeRegistration surface)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var scope = scopes[surface.ScopeKey];
        if (scope.SourceKind == null) return null;
        return new LiveRawDiscoverySource(token => scope.Role switch
        {
            "folder" or "physical-forms" or "web-root-folder" => ReadFolderFilesAsync(scope, token),
            "forms" => ReadReferencesAsync(scope, isForm: true, token),
            "views" => ReadReferencesAsync(scope, isForm: false, token),
            "welcome-page" => ReadTargetedFileAsync(scope, token),
            _ => throw new InvalidOperationException($"Unsupported live raw surface role '{scope.Role}'."),
        });
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        clientFactory.Dispose();
    }

    /// <summary>Acquires all authorized list roots independently of optional role metadata, while HOME remains targeted.</summary>
    private async Task<DiscoveryChildEnumerationResult> EnumerateWebAsync(LiveScope parent,
        CancellationToken cancellationToken)
    {
        WelcomePageAcquisition welcomePage;
        try
        {
            welcomePage = await AcquireWelcomePageAsync(parent, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (options.Intent != AspxDiscoveryIntent.HomePageOnly)
        {
            ReferenceCollector.AddReference(Candidate(AspxReferenceSourceKinds.WebWelcomePage, parent.ScopeKey,
                "PnP.Core welcome-page metadata", null, AspxReferenceDispositions.ReferenceUnavailable,
                "welcome_page_failed", null, "unavailable") with
            {
                ErrorStage = "ReadWelcomePage",
                ErrorDetail = ex.Message,
            });
            ReferenceCollector.AddGap("welcome-page:" + parent.ScopeKey + ":welcome_page_failed");
            welcomePage = new(DiscoveryTerminalOutcome.Failed, null, null, "welcome_page_failed");
        }
        if (options.Intent == AspxDiscoveryIntent.HomePageOnly)
        {
            if (welcomePage.Physical == null)
                return Result(parent, Array.Empty<LiveScope>(), welcomePage.Outcome, welcomePage.ErrorCode);

            var target = Add(new LiveScope(Key("welcome-page", parent.WebUrl.AbsoluteUri,
                    welcomePage.Physical.FileUniqueId ?? welcomePage.Locator), parent.ScopeKey,
                DiscoveryScopeKind.Folder, DiscoverySourceKind.WebWelcomePage,
                welcomePage.Locator, "welcome-page", parent.WebUrl, welcomePage.Physical.ListId,
                null, null, parent.Metadata));
            targetedFiles[target.ScopeKey] = welcomePage.Physical;
            return Result(parent, new[] { target }, DiscoveryTerminalOutcome.Complete);
        }

        IReadOnlyList<JsonElement> listItems;
        DiscoveryTerminalOutcome listOutcome;
        if (sharedLists != null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            listItems = sharedLists.Items;
            listOutcome = listItems.Count == 0 ? DiscoveryTerminalOutcome.Empty : DiscoveryTerminalOutcome.Complete;
            RecordSharedListsSurface(parent, listOutcome);
        }
        else
        {
            var endpoint = Endpoint(parent.WebUrl,
                $"_api/web/lists?$select={AllListsSelect}&$expand={AllListsExpand}");
            var listResult = await ReadCollectionAsync(parent.ScopeKey, parent.ScopeKey,
                "lists:" + parent.ScopeKey, endpoint, AllListsSelect, string.Empty,
                AspxSurfaceApplicability.Applicable, "AllListsAuthority", cancellationToken).ConfigureAwait(false);
            listItems = listResult.Items;
            listOutcome = listResult.Validation.Outcome;
            if (!IsComplete(listOutcome))
            {
                // Content-type expansion is enrichment, never the only admission route to List.RootFolder.
                var physicalEndpoint = Endpoint(parent.WebUrl,
                    $"_api/web/lists?$select={PhysicalListsSelect}&$expand=RootFolder");
                var physicalLists = await ReadCollectionAsync(parent.ScopeKey, parent.ScopeKey,
                    "physical-lists:" + parent.ScopeKey, physicalEndpoint, PhysicalListsSelect, string.Empty,
                    AspxSurfaceApplicability.Applicable, "PhysicalListsAuthorityFallback", cancellationToken)
                    .ConfigureAwait(false);
                listItems = listItems.Concat(physicalLists.Items)
                    .GroupBy(item => PropertyGuid(item, "Id")?.ToString("D") ??
                        NestedString(item, "RootFolder", "ServerRelativeUrl") ?? item.GetRawText(), StringComparer.Ordinal)
                    .Select(group => group.First()).ToArray();
                listOutcome = physicalLists.Validation.Outcome;
            }
        }
        var containers = new List<LiveScope>
        {
            Add(new LiveScope(Key("web-root", parent.WebUrl.AbsoluteUri), parent.ScopeKey,
                DiscoveryScopeKind.Container, null, parent.WebUrl.AbsoluteUri, "web-root", parent.WebUrl,
                null, null, null, parent.Metadata)),
        };
        foreach (var item in listItems)
        {
            var listId = PropertyGuid(item, "Id");
            var baseType = PropertyInt(item, "BaseType");
            var template = PropertyInt(item, "BaseTemplate");
            var rootFolder = NestedString(item, "RootFolder", "ServerRelativeUrl");
            var rootFolderUniqueId = NestedString(item, "RootFolder", "UniqueId");
            var title = PropertyString(item, "Title") ?? listId?.ToString("D") ?? "unknown-list";
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["siteCollectionId"] = parent.Metadata?.GetValueOrDefault("siteCollectionId", string.Empty) ?? string.Empty,
                ["siteUrl"] = parent.Metadata?.GetValueOrDefault("siteUrl", parent.WebUrl?.AbsoluteUri ?? string.Empty) ?? string.Empty,
                ["webId"] = parent.Metadata?.GetValueOrDefault("webId", string.Empty) ?? string.Empty,
                ["folderUniqueId"] = rootFolderUniqueId ?? string.Empty,
                ["title"] = title,
                ["baseType"] = baseType?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "missing",
                ["baseTemplate"] = template?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "missing",
                ["hidden"] = PropertyBool(item, "Hidden")?.ToString() ?? "missing",
                ["isCatalog"] = PropertyBool(item, "IsCatalog")?.ToString() ?? "missing",
                ["defaultViewUrl"] = PropertyString(item, "DefaultViewUrl") ?? string.Empty,
            };
            var scope = Add(new LiveScope(Key("list", parent.WebUrl.AbsoluteUri, listId?.ToString("D") ?? title),
                parent.ScopeKey, DiscoveryScopeKind.Container, null, rootFolder ?? title, "list", parent.WebUrl,
                listId, baseType, rootFolder, metadata));
            listRoots.Add(scope);
            RecordDisplayFormDeclarations(scope, item);
            containers.Add(scope);
            RecordListApplicability(parent, scope, template);
        }
        return Result(parent, containers, listOutcome);
    }

    /// <summary>Registers physical list roots and role-reference surfaces without a BaseType admission filter.</summary>
    private Task<DiscoveryChildEnumerationResult> EnumerateContainerAsync(LiveScope parent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (parent.Role == "web-root")
        {
            var child = Add(new LiveScope(Key("folder", parent.WebUrl.AbsoluteUri, "web-root"), parent.ScopeKey,
                DiscoveryScopeKind.Folder, DiscoverySourceKind.WebRootFiles, parent.WebUrl.AbsolutePath,
                "web-root-folder", parent.WebUrl, null, null, parent.WebUrl.AbsolutePath, parent.Metadata));
            return Task.FromResult(Result(parent, new[] { child }, DiscoveryTerminalOutcome.Complete));
        }

        var children = new List<LiveScope>();
        var decision = AspxListApplicabilityPolicy.Evaluate(parent.BaseType);
        // Every list has a folder root. Ordinary lists can own physical form ASPX files
        // there as well, so BaseType must not gate recursive file discovery.
        if (!string.IsNullOrWhiteSpace(parent.FolderUrl))
            children.Add(Add(new LiveScope(Key("folder", parent.ScopeKey, parent.FolderUrl), parent.ScopeKey,
                DiscoveryScopeKind.Folder, decision.RawLibraryRequired
                    ? DiscoverySourceKind.RawListLibraryFiles : DiscoverySourceKind.RawListFolderFiles, parent.FolderUrl,
                "folder", parent.WebUrl, parent.ListId, parent.BaseType, parent.FolderUrl, parent.Metadata)));
        if (!string.IsNullOrWhiteSpace(parent.FolderUrl))
        {
            var formsFolder = parent.FolderUrl.TrimEnd('/') + "/Forms";
            children.Add(Add(new LiveScope(Key("physical-forms", parent.ScopeKey, formsFolder),
                parent.ScopeKey, DiscoveryScopeKind.Folder, DiscoverySourceKind.RawListLibraryFiles,
                formsFolder, "physical-forms", parent.WebUrl, parent.ListId, parent.BaseType,
                formsFolder, WithMetadata(parent.Metadata, ("folderUniqueId", string.Empty)))));
        }
        children.Add(Add(new LiveScope(Key("forms", parent.WebUrl.AbsoluteUri, parent.ListId?.ToString("D") ?? parent.ScopeKey),
            parent.ScopeKey, DiscoveryScopeKind.Folder, DiscoverySourceKind.ListFormBackingFiles,
            parent.Locator + "/Forms REST objects", "forms", parent.WebUrl, parent.ListId,
            parent.BaseType, parent.FolderUrl, parent.Metadata)));
        children.Add(Add(new LiveScope(Key("views", parent.WebUrl.AbsoluteUri, parent.ListId?.ToString("D") ?? parent.ScopeKey),
            parent.ScopeKey, DiscoveryScopeKind.Folder, DiscoverySourceKind.ListViewBackingFiles,
            parent.Locator + "/Views REST objects", "views", parent.WebUrl, parent.ListId,
            parent.BaseType, parent.FolderUrl, parent.Metadata)));
        // BaseType describes a list; it cannot veto an authorized physical root.
        var outcome = string.IsNullOrWhiteSpace(parent.FolderUrl)
            ? DiscoveryTerminalOutcome.Unknown : DiscoveryTerminalOutcome.Complete;
        return Task.FromResult(Result(parent, children, outcome,
            outcome == DiscoveryTerminalOutcome.Unknown ? "list_root_folder_missing" : null));
    }

    /// <summary>Enumerates each parent-bound folder scope while reusing only terminal, complete physical folder contents.</summary>
    private async Task<DiscoveryChildEnumerationResult> EnumerateFolderAsync(LiveScope parent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (parent.Role is "forms" or "views" or "welcome-page")
        {
            return Result(parent, Array.Empty<LiveScope>(), DiscoveryTerminalOutcome.Empty);
        }

        var modeled = await TryReadCompleteModeledFolderAsync(parent, cancellationToken).ConfigureAwait(false);
        if (modeled != null)
        {
            RecordModeledFolderSurface(parent, modeled, files: false);
            var children = modeled.Folders.Select(item => FolderChild(parent, item.ServerRelativeUrl, item.UniqueId))
                .Where(child => child != null).ToArray();
            return Result(parent, children, invalidFolderScopes.Contains(parent.ScopeKey)
                ? DiscoveryTerminalOutcome.Unknown : modeled.Outcome);
        }

        var result = await ReadFolderCollectionAsync(parent, files: false, cancellationToken).ConfigureAwait(false);
        var rawChildren = result.Items.Select(item => FolderChild(parent,
                PropertyString(item, "ServerRelativeUrl"), PropertyString(item, "UniqueId")))
            .Where(child => child != null).ToArray();
        return Result(parent, rawChildren, invalidFolderScopes.Contains(parent.ScopeKey)
            ? DiscoveryTerminalOutcome.Unknown : result.Validation.Outcome);
    }

    /// <summary>Keeps folder scopes specific to their parent and rejects missing or cyclic locators without inventing identities.</summary>
    private LiveScope FolderChild(LiveScope parent, string folderUrl, string folderUniqueId)
    {
        if (string.IsNullOrWhiteSpace(folderUrl))
        {
            invalidFolderScopes.Add(parent.ScopeKey);
            ReferenceCollector.AddGap("folders:" + parent.ScopeKey + ":folder_url_missing");
            return null;
        }
        for (var ancestor = parent; ancestor != null; ancestor = ancestor.ParentScopeKey != null
            ? scopes.GetValueOrDefault(ancestor.ParentScopeKey) : null)
        {
            if (ancestor.Kind == DiscoveryScopeKind.Folder &&
                string.Equals(FolderIdentity(ancestor), FolderIdentity(parent with { FolderUrl = folderUrl }),
                    StringComparison.OrdinalIgnoreCase))
            {
                invalidFolderScopes.Add(parent.ScopeKey);
                ReferenceCollector.AddGap("folders:" + parent.ScopeKey + ":folder_cycle");
                return null;
            }
        }
        if (Guid.TryParse(folderUniqueId, out var observedId) && observedId != Guid.Empty)
        {
            observedFolderIds[FolderIdentity(parent with { FolderUrl = folderUrl })] = observedId.ToString("D");
        }
        var owner = PhysicalOwner(parent, folderUrl);
        return Add(new LiveScope(Key("folder", parent.ScopeKey, folderUrl), parent.ScopeKey,
            DiscoveryScopeKind.Folder, parent.SourceKind, folderUrl, "folder", parent.WebUrl,
            owner?.ListId, owner?.BaseType, folderUrl,
            WithMetadata(owner?.Metadata ?? parent.Metadata, ("folderUniqueId", folderUniqueId ?? string.Empty))));
    }

    /// <summary>Emits GUID-backed file facts only from physical folder authority, once per file across overlapping folder entrances.</summary>
    private async IAsyncEnumerable<RawDiscoveryBatch> ReadFolderFilesAsync(LiveScope scope,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        scope = PhysicalScope(scope);
        var modeled = await TryReadCompleteModeledFolderAsync(scope, cancellationToken).ConfigureAwait(false);
        if (modeled != null)
        {
            RecordModeledFolderSurface(scope, modeled, files: true);
            var records = ObservePhysicalRecords(scope, modeled.Files.Select(item => new RawDiscoveryRecord(
                item.UniqueId, item.UniqueId, scope.ScopeKey, item.Name, item.ServerRelativeUrl, true,
                options.PermissionContext, new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["actualProvider"] = modeled.Provider,
                    ["actualOperation"] = modeled.Operation,
                    ["customizedPageStatus"] = item.CustomizedPageStatus ?? "unknown",
                },
                SiteCollectionId: MetadataGuid(scope.Metadata, "siteCollectionId"),
                WebId: MetadataGuid(scope.Metadata, "webId"),
                ListId: scope.ListId,
                FolderUniqueId: MetadataGuid(scope.Metadata, "folderUniqueId") ??
                    (Guid.TryParse(modeled.FolderUniqueId, out var modeledFolderId) && modeledFolderId != Guid.Empty
                        ? modeledFolderId : null),
                HomePage: IsHomePage(scope, item.ServerRelativeUrl),
                LibraryHidden: MetadataBool(scope.Metadata, "hidden"),
                CustomizedPageStatusRaw: item.CustomizedPageStatus,
                SetupPath: item.SetupPath,
                ObservationMethod: modeled.Provider + ":" + modeled.Operation,
                WelcomePageStatus: HomePageStatus(scope, item.ServerRelativeUrl),
                SiteUrl: MetadataString(scope.Metadata, "siteUrl") ?? scope.WebUrl?.GetLeftPart(UriPartial.Authority),
                WebUrl: scope.WebUrl?.AbsoluteUri)));
            var invalid = invalidFileIdentityScopes.Contains(scope.ScopeKey);
            var outcome = invalid ? DiscoveryTerminalOutcome.Unknown : modeled.Files.Count == 0
                ? DiscoveryTerminalOutcome.Empty : modeled.Outcome;
            yield return new RawDiscoveryBatch(0,
                DiscoveryHash.Of(modeled.Provider, modeled.Operation, scope.FolderUrl),
                DiscoveryHash.Of(string.Join('|', modeled.Files.Select(item => item.UniqueId))), records,
                IsTerminal: true, outcome, GapCode: invalid ? MissingFileIdentity : null);
            yield break;
        }

        var result = await ReadFolderCollectionAsync(scope, files: true, cancellationToken).ConfigureAwait(false);
        foreach (var page in result.Pages)
        {
            var records = ObservePhysicalRecords(scope, page.Page.Items.Select(item => new RawDiscoveryRecord(
                PropertyString(item, "UniqueId"), PropertyString(item, "UniqueId"),
                scope.ListId?.ToString("D") ?? scope.ScopeKey, PropertyString(item, "Name"),
                PropertyString(item, "ServerRelativeUrl"), true, options.PermissionContext,
                EvidenceMetadata(page.Page.RequestUri, page.Receipt.ActualSelect, string.Empty, page.Page.SchemaFlavor,
                    ("customizedPageStatus", PropertyString(item, "CustomizedPageStatus") ?? "unknown"),
                    ("length", PropertyString(item, "Length") ?? "unknown")),
                SiteCollectionId: MetadataGuid(scope.Metadata, "siteCollectionId"),
                WebId: MetadataGuid(scope.Metadata, "webId"),
                ListId: scope.ListId,
                FolderUniqueId: MetadataGuid(scope.Metadata, "folderUniqueId"),
                ListItemId: NestedInt(item, "ListItemAllFields", "Id"),
                HomePage: IsHomePage(scope, PropertyString(item, "ServerRelativeUrl")),
                ContentTypeId: NestedString(item, "ListItemAllFields", "ContentTypeId"),
                PageType: InferPageType(NestedString(item, "ListItemAllFields", "ContentTypeId")),
                LibraryHidden: MetadataBool(scope.Metadata, "hidden"),
                CustomizedPageStatusRaw: PropertyString(item, "CustomizedPageStatus"),
                SetupPath: NestedString(item, "Properties", RestSetupPathProperty),
                ObservationMethod: "SharePoint REST folder files",
                WelcomePageStatus: HomePageStatus(scope, PropertyString(item, "ServerRelativeUrl")),
                SiteUrl: MetadataString(scope.Metadata, "siteUrl") ?? scope.WebUrl?.GetLeftPart(UriPartial.Authority),
                WebUrl: scope.WebUrl?.AbsoluteUri)));
            var batch = ToRawBatch(page, records, result.Validation.Outcome);
            if (invalidFileIdentityScopes.Contains(scope.ScopeKey))
            {
                batch = batch with
                {
                    TerminalOutcome = batch.IsTerminal && IsComplete(batch.TerminalOutcome)
                        ? DiscoveryTerminalOutcome.Unknown : batch.TerminalOutcome,
                    GapCode = MissingFileIdentity,
                    GapDetail = "Folder authority returned an ASPX file without a real File.UniqueId; no physical identity was minted.",
                };
            }
            yield return batch;
        }
    }

    /// <summary>Retains missing-identity gaps and suppresses duplicate physical file facts without deriving GUIDs from locators.</summary>
    private IReadOnlyList<RawDiscoveryRecord> ObservePhysicalRecords(LiveScope scope, IEnumerable<RawDiscoveryRecord> records)
    {
        var observed = new List<RawDiscoveryRecord>();
        foreach (var record in records)
        {
            if (!Guid.TryParse(record.FileUniqueId, out var fileId) || fileId == Guid.Empty)
            {
                if (AspxAdmission.Evaluate(record).IsAspx)
                {
                    invalidFileIdentityScopes.Add(scope.ScopeKey);
                    ReferenceCollector.AddGap("files:" + scope.ScopeKey + ":" + MissingFileIdentity);
                }
                continue;
            }
            var identity = fileId.ToString("D");
            if (!emittedFileScopes.TryAdd(identity, scope.ScopeKey))
            {
                continue;
            }
            observed.Add(record);
        }
        return observed;
    }

    private async IAsyncEnumerable<RawDiscoveryBatch> ReadTargetedFileAsync(LiveScope scope,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!targetedFiles.TryGetValue(scope.ScopeKey, out var record))
            throw new InvalidOperationException($"Targeted ASPX file '{scope.ScopeKey}' was not acquired.");
        await Task.CompletedTask.ConfigureAwait(false);
        yield return new RawDiscoveryBatch(0,
            DiscoveryHash.Of("welcome-page", scope.ScopeKey, record.PhysicalLocator),
            DiscoveryHash.Of(record.FileUniqueId, record.PhysicalLocator), new[] { record },
            IsTerminal: true, DiscoveryTerminalOutcome.Complete);
    }

    /// <summary>Enumerates Forms or Views as declaration-only evidence with their original pagination and system-list disposition.</summary>
    private async IAsyncEnumerable<RawDiscoveryBatch> ReadReferencesAsync(LiveScope scope, bool isForm,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var select = isForm ? FormsSelect : ViewsSelect;
        var name = isForm ? "Forms" : "Views";
        var endpoint = Endpoint(scope.WebUrl,
            $"_api/web/lists(guid'{scope.ListId:D}')/{name}?$select={select}");
        var dispositionRule = isForm
            ? AspxSystemListFormsPolicy.Create(scope.Metadata, scope.FolderUrl, options.PlatformBuildRef)
            : null;
        var result = await ReadCollectionAsync(scope.ScopeKey, scope.ParentScopeKey,
            name.ToLowerInvariant() + ":" + scope.ScopeKey, endpoint, select, string.Empty,
            scope.BaseType == (int)ListBaseType.DocumentLibrary
                ? AspxSurfaceApplicability.Applicable : AspxSurfaceApplicability.SystemOrVirtualOnly,
            isForm ? "AllListFormsAuthority" : "AllListViewsAuthority", cancellationToken,
            dispositionRule).ConfigureAwait(false);
        if (result.AppliedDispositionRule != null)
        {
            var evidence = new List<string>(result.AppliedDispositionRule.EvidenceRefs)
            {
                EvidenceRef(endpoint, result.Pages.FirstOrDefault()?.Page, select),
            };
            ReferenceCollector.AddReference(Candidate(AspxReferenceSourceKinds.ListForm,
                (scope.ListId?.ToString("D") ?? scope.ScopeKey) + ":forms-authority",
                "SharePoint REST List.Forms system-list disposition", scope.FolderUrl?.TrimEnd('/') + "/Forms",
                AspxReferenceDispositions.ReferenceUnavailable, result.AppliedDispositionRule.ReasonCode,
                null, "system-or-virtual-unknown", evidence.ToArray()));
        }
        foreach (var page in result.Pages)
        {
            foreach (var item in page.Page.Items)
            {
                var objectId = PropertyString(item, "Id") ?? DiscoveryHash.Of(item.GetRawText());
                ReferenceCollector.AddReference(DeclaredReference(scope,
                    isForm ? AspxReferenceSourceKinds.ListForm : AspxReferenceSourceKinds.ListView,
                    objectId, PropertyString(item, "ServerRelativeUrl"),
                    "SharePoint REST List." + name, EvidenceRef(endpoint, page.Page, select)));
            }
            // Metadata enumerations retain their own pagination and failures, but cannot mint a physical file.
            yield return ToRawBatch(page, Array.Empty<RawDiscoveryRecord>(), result.Validation.Outcome);
        }
    }

    /// <summary>Records a form, view, or welcome-page locator as role evidence without resolving it into inventory authority.</summary>
    private AspxReferenceCandidate DeclaredReference(LiveScope scope, string sourceKind, string objectId,
        string locator, string method, params string[] evidence)
    {
        var canonical = ReferenceLocator(scope, locator);
        var disposition = string.IsNullOrWhiteSpace(locator) ? AspxReferenceDispositions.Unknown
            : string.Equals(Path.GetExtension(canonical), ".aspx", StringComparison.OrdinalIgnoreCase)
                ? AspxReferenceDispositions.Declared : AspxReferenceDispositions.NonAspx;
        return Candidate(sourceKind, scope.ListId == null ? objectId : scope.ListId.Value.ToString("D") + ":" + objectId,
            method, canonical, disposition, string.IsNullOrWhiteSpace(locator) ? "locator_missing" : null,
            null, "declared-reference", evidence) with { ListId = scope.ListId };
    }

    /// <summary>Normalizes tenant-local role locators but keeps external origins intact so path-only enrichment cannot mislink them.</summary>
    private static string ReferenceLocator(LiveScope scope, string locator)
    {
        if (Uri.TryCreate(locator, UriKind.Absolute, out var absolute) &&
            (absolute.Scheme == Uri.UriSchemeHttps || absolute.Scheme == Uri.UriSchemeHttp) &&
            !string.Equals(absolute.GetLeftPart(UriPartial.Authority), scope.WebUrl.GetLeftPart(UriPartial.Authority),
                StringComparison.OrdinalIgnoreCase))
        {
            return locator;
        }
        return DisplayFormUrlPolicy.ToServerRelative(locator, scope.WebUrl.AbsolutePath,
            Uri.TryCreate(MetadataString(scope.Metadata, "siteUrl"), UriKind.Absolute, out var site)
                ? site.AbsolutePath : scope.WebUrl.AbsolutePath) ?? locator;
    }

    /// <summary>
    /// Records list and registered content-type display-form declarations without querying list items.
    /// A declaration identifies a possible renderer; it does not assert an item count or a page address.
    /// </summary>
    private void RecordDisplayFormDeclarations(LiveScope list, JsonElement row)
    {
        var forms = ReadDisplayForms(row);
        var sourceId = list.ListId?.ToString("D") ?? list.ScopeKey;
        var method = sharedLists != null ? "PnP.Core shared Web.Lists metadata" : "SharePoint REST _api/web/lists metadata";
        RecordDisplayFormDeclaration(list, AspxReferenceSourceKinds.ListDefaultDisplayForm, sourceId + ":default",
            method, forms.DefaultFormUrl);
        foreach (var form in forms.ContentTypeForms)
        {
            RecordDisplayFormDeclaration(list, AspxReferenceSourceKinds.ContentTypeDisplayForm,
                sourceId + ":ct:" + form.ContentTypeId, method, form.FormUrl, form.ContentTypeId);
        }
    }

    /// <summary>Retains each list or CT declaration independently without resolving or creating a file fact.</summary>
    private void RecordDisplayFormDeclaration(LiveScope list, string kind, string objectId, string method,
        string rawUrl, string contentTypeId = null)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return;
        }
        ReferenceCollector.AddReference(Candidate(kind, objectId, method, ReferenceLocator(list, rawUrl),
            AspxReferenceDispositions.Declared, null, null, "declared-display-form") with
        {
            ListId = list.ListId,
            ContentTypeId = contentTypeId,
        });
    }

    /// <summary>Resolves the explicitly requested HOME file and requires a real GUID; full-inventory role declarations never call this path.</summary>
    private async Task<(AspxReferenceCandidate Candidate, RawDiscoveryRecord Physical,
        DiscoveryTerminalOutcome Outcome)> ResolveReferenceAsync(
        LiveScope scope, bool isForm, string objectId, string locator, string evidenceRef,
        CancellationToken cancellationToken, string sourceKindOverride = null, string methodOverride = null)
    {
        var sourceKind = sourceKindOverride ??
            (isForm ? AspxReferenceSourceKinds.ListForm : AspxReferenceSourceKinds.ListView);
        var sourceObjectIdentity = scope.ListId == null ? objectId : scope.ListId.Value.ToString("D") + ":" + objectId;
        var method = methodOverride ?? (isForm ? "SharePoint REST List.Forms + PnP.Core file resolution"
            : "SharePoint REST List.Views + PnP.Core file resolution");
        if (string.IsNullOrWhiteSpace(locator))
            return (Candidate(sourceKind, sourceObjectIdentity, method, locator, AspxReferenceDispositions.Unknown,
                "locator_missing", null, "unknown", evidenceRef), null, DiscoveryTerminalOutcome.Unknown);
        if (!string.Equals(Path.GetExtension(locator), ".aspx", StringComparison.OrdinalIgnoreCase))
            return (Candidate(sourceKind, sourceObjectIdentity, method, locator, AspxReferenceDispositions.NonAspx,
                "locator_not_aspx", null, "not-applicable", evidenceRef), null, DiscoveryTerminalOutcome.Empty);

        var client = await clientFactory.GetAsync(scope.WebUrl, cancellationToken).ConfigureAwait(false);
        var resolved = await client.ResolveFileAsync(locator, cancellationToken).ConfigureAwait(false);
        if (resolved.Outcome != DiscoveryTerminalOutcome.Complete ||
            !Guid.TryParse(resolved.FileUniqueId, out var resolvedId) || resolvedId == Guid.Empty)
        {
            var disposition = resolved.Outcome is DiscoveryTerminalOutcome.Denied or DiscoveryTerminalOutcome.Failed
                ? AspxReferenceDispositions.ReferenceUnavailable : AspxReferenceDispositions.Unknown;
            return (Candidate(sourceKind, sourceObjectIdentity, method, locator, disposition,
                resolved.ErrorCode ?? (resolved.Outcome == DiscoveryTerminalOutcome.Complete ? MissingFileIdentity : null),
                null, "unavailable", evidenceRef, resolved.EvidenceRef) with
                {
                    ErrorStage = resolved.ErrorStage,
                    ErrorDetail = resolved.ErrorDetail,
                }, null, resolved.Outcome == DiscoveryTerminalOutcome.Complete
                    ? DiscoveryTerminalOutcome.Unknown : resolved.Outcome);
        }

        var ghosted = string.Equals(resolved.CustomizedPageStatus, nameof(CustomizedPageStatus.Uncustomized),
            StringComparison.OrdinalIgnoreCase);
        var dispositionValue = ghosted ? AspxReferenceDispositions.LinkedPhysicalGhosted
            : AspxReferenceDispositions.LinkedPhysicalCustomized;
        var contentOrigin = ghosted ? "verified-ghosted" : "verified-customized-or-physical";
        var physicalOwnerListId = options.Intent == AspxDiscoveryIntent.HomePageOnly ? resolved.ListId : null;
        var physical = new RawDiscoveryRecord(objectId, resolved.FileUniqueId,
            physicalOwnerListId?.ToString("D") ?? scope.ListId?.ToString("D") ?? scope.ScopeKey,
            resolved.Name ?? Path.GetFileName(locator),
            resolved.ServerRelativeUrl ?? locator, true, options.PermissionContext,
            EvidenceMetadata(new Uri(scope.WebUrl, locator), string.Empty, string.Empty, "pnp-file-resolution",
                ("referenceSourceKind", sourceKind), ("referenceObjectId", objectId),
                ("referenceListId", physicalOwnerListId?.ToString("D") ?? scope.ListId?.ToString("D") ?? string.Empty),
                ("customizedPageStatus", resolved.CustomizedPageStatus ?? "unknown")),
            SiteCollectionId: MetadataGuid(scope.Metadata, "siteCollectionId"),
            WebId: MetadataGuid(scope.Metadata, "webId"),
            // A List.Views URL may point to a page in another library. ResolveFileAsync returns
            // the physical owner for targeted home-page acquisition. Full inventory retains the
            // richer owner observed through the raw list-file surface during evidence merging.
            ListId: physicalOwnerListId,
            FolderUniqueId: null,
            ListItemId: resolved.ListItemId,
            HomePage: IsHomePage(scope, resolved.ServerRelativeUrl ?? locator),
            ContentTypeId: resolved.ContentTypeId,
            PageType: InferPageType(resolved.ContentTypeId),
            LibraryHidden: null,
            CustomizedPageStatusRaw: resolved.CustomizedPageStatus,
            ObservationMethod: method,
            WelcomePageStatus: HomePageStatus(scope, resolved.ServerRelativeUrl ?? locator),
            SiteUrl: MetadataString(scope.Metadata, "siteUrl") ?? scope.WebUrl?.GetLeftPart(UriPartial.Authority),
            WebUrl: scope.WebUrl?.AbsoluteUri);
        return (Candidate(sourceKind, sourceObjectIdentity, method, locator, dispositionValue, null,
            resolved.FileUniqueId, contentOrigin, evidenceRef, resolved.EvidenceRef), physical,
            DiscoveryTerminalOutcome.Complete);
    }

    /// <summary>Records the welcome-page declaration for inventory and resolves a physical file only for explicit HOME acquisition.</summary>
    private async Task<WelcomePageAcquisition> AcquireWelcomePageAsync(LiveScope web,
        CancellationToken cancellationToken)
    {
        var client = await clientFactory.GetAsync(web.WebUrl, cancellationToken).ConfigureAwait(false);
        var result = await client.ReadWelcomePageAsync(cancellationToken).ConfigureAwait(false);
        var outcome = result.Outcome == DiscoveryTerminalOutcome.Complete && string.IsNullOrWhiteSpace(result.Value)
            ? DiscoveryTerminalOutcome.Empty : result.Outcome;
        RecordModeledValueSurface(web, "welcome-page:" + web.ScopeKey, result, outcome,
            AspxSurfaceApplicability.SystemOrVirtualOnly, "PnPCoreWelcomePageAuthority");
        if (outcome is DiscoveryTerminalOutcome.Denied or DiscoveryTerminalOutcome.Failed or
            DiscoveryTerminalOutcome.Unknown)
        {
            ReferenceCollector.AddReference(Candidate(AspxReferenceSourceKinds.WebWelcomePage, web.ScopeKey,
                result.Provider + ":" + result.Operation, null,
                AspxReferenceDispositions.ReferenceUnavailable,
                result.ErrorCode ?? (outcome == DiscoveryTerminalOutcome.Denied
                    ? "welcome_page_denied" : "welcome_page_failed"),
                null, "unavailable", result.EvidenceRef));
            return new(outcome, null, null, result.ErrorCode);
        }
        var value = result.Value;
        if (string.IsNullOrWhiteSpace(value))
        {
            ReferenceCollector.AddReference(Candidate(AspxReferenceSourceKinds.WebWelcomePage, web.ScopeKey,
                result.Provider + ":" + result.Operation, value, AspxReferenceDispositions.NonAspx,
                "welcome_page_success_empty", null, "empty", result.EvidenceRef));
            return new(DiscoveryTerminalOutcome.Empty, value, null, null);
        }
        var absolutePath = value.StartsWith('/') ? value : web.WebUrl.AbsolutePath.TrimEnd('/') + "/" + value.TrimStart('/');
        welcomePageByWeb[web.WebUrl.AbsoluteUri.TrimEnd('/')] = NormalizeServerRelativePath(absolutePath);
        if (options.Intent != AspxDiscoveryIntent.HomePageOnly)
        {
            ReferenceCollector.AddReference(DeclaredReference(web, AspxReferenceSourceKinds.WebWelcomePage,
                web.ScopeKey, absolutePath, result.Provider + ":" + result.Operation, result.EvidenceRef));
            return new(outcome, absolutePath, null, null);
        }
        var resolved = await ResolveReferenceAsync(web with { ListId = null }, isForm: false,
            web.ScopeKey, absolutePath, result.EvidenceRef,
            cancellationToken, AspxReferenceSourceKinds.WebWelcomePage,
            result.Provider + ":" + result.Operation + " + PnP.Core file resolution").ConfigureAwait(false);
        ReferenceCollector.AddReference(resolved.Candidate with
        {
            SourceKind = AspxReferenceSourceKinds.WebWelcomePage,
            AcquisitionMethod = result.Provider + ":" + result.Operation + " + PnP.Core file resolution",
        });
        var physical = resolved.Physical == null ? null : resolved.Physical with
        {
            PhysicalLocator = NormalizeServerRelativePath(absolutePath),
            HomePage = true,
            WelcomePageStatus = "Matched",
        };
        return new(resolved.Outcome, absolutePath, physical, resolved.Candidate.ReasonCode);
    }

    private void RecordSharedListsSurface(LiveScope web, DiscoveryTerminalOutcome outcome)
    {
        var digest = DiscoveryHash.Of(sharedLists.Items.Select(item => item.GetRawText()).ToArray());
        const string operation = "PnP.Core:Web.Lists(raw Web response projection)";
        var row = SurfaceRow(web.ScopeKey, web.ScopeKey, "lists:" + web.ScopeKey,
            operation, AllListsSelect, string.Empty, "PnPCoreSharedListMetadataAuthority",
            AspxSurfaceApplicability.Applicable, outcome, sharedLists.Items.Count,
            AspxExpectedCountState.Known, sharedLists.Items.Count,
            DiscoveryHash.Of(sharedLists.WebUrl.AbsoluteUri, digest), 0,
            new[] { "shared-sdk-web-lists:" + digest, "provider=PnP.Core", "operation=Web.Lists",
                "complete-raw-expansion;no-continuation-markers;discovery-fields-projected" },
            "AllListsAuthority", outcome == DiscoveryTerminalOutcome.Empty ? "AuthorityTerminalZero" : null,
            outcome == DiscoveryTerminalOutcome.Empty ? sharedLists.WebUrl.AbsoluteUri : null) with
        {
            ActualMethod = "MODEL",
            ActualEndpoint = operation,
            AsOfUtc = sharedLists.ReceivedAtUtc,
        };
        // There was no new REST request, so do not manufacture HTTP pagination receipts.
        ReferenceCollector.AddSurface(row, Array.Empty<AspxPaginationPageReceipt>());
    }

    /// <summary>
    /// Records actual BaseType provenance and per-surface applicability for legacy library, physical-root, Forms-tree, and form/view authorities.
    /// Keeps missing authority unknown and distinguishes library classification from admission of all observed list roots.
    /// </summary>
    private void RecordListApplicability(LiveScope web, LiveScope list, int? template)
    {
        var decision = AspxListApplicabilityPolicy.Evaluate(list.BaseType);
        var applicability = decision.Applicability;
        var counterexample = decision.RuntimeCounterexampleState;
        var outcome = decision.Outcome;
        var expectedState = outcome is DiscoveryTerminalOutcome.Complete or DiscoveryTerminalOutcome.Empty
            ? AspxExpectedCountState.Known : AspxExpectedCountState.Unknown;
        var evidence = new[]
        {
            $"actual-BaseType={list.BaseType?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "missing"}",
            $"BaseTemplate={template?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "missing"}",
            $"surface-family={ClassifyListSurface(list, template)}",
            $"Hidden={list.Metadata?.GetValueOrDefault("hidden", "missing") ?? "missing"}",
            $"IsCatalog={list.Metadata?.GetValueOrDefault("isCatalog", "missing") ?? "missing"}",
            "Document-library classification uses actual BaseType; every observed list root is traversed for physical files.",
        };
        ReferenceCollector.AddSurface(SurfaceRow(web.ScopeKey, web.ScopeKey,
            "list-applicability:" + list.ScopeKey, list.Locator, string.Empty, string.Empty,
            "ActualListBaseTypeAuthority", applicability, outcome,
            expectedState == AspxExpectedCountState.Known ? 1 : null, expectedState,
            1, DiscoveryHash.Of("list-applicability", list.ScopeKey), 0, evidence,
            "list-root-files+physical-forms+forms+views") with
            {
                ActualMethod = sharedLists == null ? "GET" : "MODEL",
                ActualEndpoint = sharedLists == null ? list.Locator : "PnP.Core:Web.Lists(raw Web response projection)",
                AsOfUtc = sharedLists?.ReceivedAtUtc ?? DateTimeOffset.UtcNow,
            }, Array.Empty<AspxPaginationPageReceipt>(),
            outcome == DiscoveryTerminalOutcome.Unknown
                ? new[] { counterexample == AspxRuntimeCounterexampleState.Observed
                    ? "not_applicable_runtime_counterexample" : "invalid_or_unspecified_base_type" }
                : Array.Empty<string>());

        RecordMatrixDisposition(web, list, "document-library-files",
            decision.RawLibraryRequired ? AspxSurfaceApplicability.Applicable
                : list.BaseType == null ? AspxSurfaceApplicability.Unknown
                : AspxSurfaceApplicability.SystemOrVirtualOnly,
            list.BaseType == null ? DiscoveryTerminalOutcome.Unknown : DiscoveryTerminalOutcome.Complete,
            "DocumentLibraryFilesAuthority", evidence);
        RecordMatrixDisposition(web, list, "list-root-files",
            string.IsNullOrWhiteSpace(list.FolderUrl) ? AspxSurfaceApplicability.Unknown
                : AspxSurfaceApplicability.Applicable,
            string.IsNullOrWhiteSpace(list.FolderUrl) ? DiscoveryTerminalOutcome.Unknown
                : DiscoveryTerminalOutcome.Complete,
            "ListRootFilesAuthority", evidence);
        RecordMatrixDisposition(web, list, "physical-forms-tree",
            string.IsNullOrWhiteSpace(list.FolderUrl) ? AspxSurfaceApplicability.Unknown
                : AspxSurfaceApplicability.Applicable,
            string.IsNullOrWhiteSpace(list.FolderUrl) ? DiscoveryTerminalOutcome.Unknown
                : DiscoveryTerminalOutcome.Complete,
            "PhysicalFormsTreeAuthority", evidence);
        RecordMatrixDisposition(web, list, "list-forms", decision.Applicability, decision.Outcome,
            "AllListFormsAuthority", evidence);
        RecordMatrixDisposition(web, list, "list-views", decision.Applicability, decision.Outcome,
            "AllListViewsAuthority", evidence);
    }

    private void RecordMatrixDisposition(LiveScope web, LiveScope list, string surface,
        AspxSurfaceApplicability applicability, DiscoveryTerminalOutcome outcome, string adapter,
        IReadOnlyList<string> evidence)
    {
        var known = outcome is DiscoveryTerminalOutcome.Complete or DiscoveryTerminalOutcome.Empty;
        var row = SurfaceRow(web.ScopeKey, web.ScopeKey,
            $"applicability:{list.ScopeKey}:{surface}", list.Locator, string.Empty, string.Empty,
            "ReviewedApplicabilityMatrix", applicability, outcome, known ? 1 : null,
            known ? AspxExpectedCountState.Known : AspxExpectedCountState.Unknown, known ? 1 : 0,
            DiscoveryHash.Of("applicability", list.ScopeKey, surface, applicability.ToString(),
                outcome.ToString()), 0, evidence, adapter) with
        {
            ActualMethod = "CLASSIFY",
            ClassificationEffect = applicability == AspxSurfaceApplicability.Applicable
                ? "physical-and-reference" : applicability == AspxSurfaceApplicability.SystemOrVirtualOnly
                    ? "reference-first" : "fail-closed",
        };
        ReferenceCollector.AddSurface(row,
            Array.Empty<AspxPaginationPageReceipt>(), known ? Array.Empty<string>()
                : new[] { $"applicability:{list.ScopeKey}:{surface}:unknown" });
    }

    private static string ClassifyListSurface(LiveScope list, int? template)
    {
        var catalog = string.Equals(list.Metadata?.GetValueOrDefault("isCatalog"), "True",
            StringComparison.OrdinalIgnoreCase);
        if (catalog || list.FolderUrl?.Contains("/_catalogs/", StringComparison.OrdinalIgnoreCase) == true)
            return template == 116 || list.FolderUrl?.Contains("masterpage", StringComparison.OrdinalIgnoreCase) == true
                ? "catalog-page-layout-library" : "catalog-library";
        if (template is 119 or 850) return "page-library";
        return list.BaseType == (int)ListBaseType.DocumentLibrary ? "document-library" : "list";
    }

    /// <summary>Returns a complete modeled folder snapshot across entrances, while incomplete model reads remain local and fall back to REST.</summary>
    private async Task<SharePointModeledFolderResult> TryReadCompleteModeledFolderAsync(LiveScope scope,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = FolderIdentity(scope);
        if (completeModeledFolders.TryGetValue(key, out var complete))
        {
            return complete;
        }
        if (scope.SourceKind != DiscoverySourceKind.WebRootFiles ||
            folderCollections.ContainsKey(key + "|Files") || folderCollections.ContainsKey(key + "|Folders"))
        {
            return null;
        }
        if (!modeledFolders.TryGetValue(scope.ScopeKey, out var result))
        {
            var client = await clientFactory.GetAsync(scope.WebUrl, cancellationToken).ConfigureAwait(false);
            result = await client.ReadFolderAsync(scope.FolderUrl, cancellationToken).ConfigureAwait(false);
            modeledFolders[scope.ScopeKey] = result;
        }
        if (IsComplete(result.Outcome))
        {
            if (result.Files.All(item => Guid.TryParse(item.UniqueId, out var id) && id != Guid.Empty))
            {
                completeModeledFolders[key] = result;
            }
            return result;
        }
        RecordModeledFolderSurface(scope, result, files: true);
        RecordModeledFolderSurface(scope, result, files: false);
        return null;
    }

    /// <summary>Shares successful Files/Folders collection content without collapsing the distinct parent scopes or fabricating HTTP receipts.</summary>
    private async Task<LiveCollectionResult> ReadFolderCollectionAsync(LiveScope scope, bool files,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var child = files ? "Files" : "Folders";
        var key = FolderIdentity(scope) + "|" + child;
        if (folderCollections.TryGetValue(key, out var existing))
        {
            if (existing.Surface.ScopeKey != scope.ScopeKey)
            {
                ReferenceCollector.AddSurface(existing.Surface with
                {
                    ScopeKey = scope.ScopeKey,
                    ParentScopeKey = scope.ParentScopeKey,
                    SurfaceId = child.ToLowerInvariant() + ":" + scope.ScopeKey,
                    ActualMethod = "CACHE",
                    EvidenceRefs = existing.Surface.EvidenceRefs.Concat(new[]
                    {
                        "reused-surface=" + existing.Surface.SurfaceId,
                        "reused-pagination-chain=" + existing.Surface.PaginationChainHash,
                    }).ToArray(),
                }, Array.Empty<AspxPaginationPageReceipt>());
            }
            return existing;
        }

        var select = files ? FilesSelect : FoldersSelect;
        var endpoint = FolderEndpoint(scope, child, select, files ? FilesExpand : null);
        var adapter = !files ? "RecursiveFolderAuthority" : scope.Role == "physical-forms"
            ? "PhysicalFormsTreeAuthority" : scope.SourceKind == DiscoverySourceKind.RawListFolderFiles
                ? "ListRootFilesAuthority" : "DocumentLibraryFilesAuthority";
        var result = await ReadCollectionAsync(scope.ScopeKey, scope.ParentScopeKey,
            child.ToLowerInvariant() + ":" + scope.ScopeKey, endpoint, select, string.Empty,
            AspxSurfaceApplicability.Applicable, adapter, cancellationToken).ConfigureAwait(false);
        if (files && (result.Validation.Outcome is DiscoveryTerminalOutcome.Denied or DiscoveryTerminalOutcome.Failed))
        {
            // ListItemAllFields and property bags are optional enrichment, not a physical-file admission requirement.
            const string physicalSelect = "UniqueId,Name,ServerRelativeUrl,CustomizedPageStatus,Length";
            var enriched = result;
            result = await ReadCollectionAsync(scope.ScopeKey, scope.ParentScopeKey,
                "physical-files:" + scope.ScopeKey, FolderEndpoint(scope, child, physicalSelect), physicalSelect,
                string.Empty, AspxSurfaceApplicability.Applicable, "PhysicalFolderFilesAuthorityFallback",
                cancellationToken).ConfigureAwait(false);
            if (!IsComplete(result.Validation.Outcome) && enriched.Items.Count > 0)
            {
                // A failed fallback cannot erase physical observations from an earlier partial page chain.
                result = enriched;
            }
        }
        if (!files && result.Items.Any(item => string.IsNullOrWhiteSpace(PropertyString(item, "ServerRelativeUrl"))))
        {
            var incomplete = result.Surface with
            {
                TerminalOutcome = DiscoveryTerminalOutcome.Unknown,
                ExpectedCount = null,
                ExpectedCountState = AspxExpectedCountState.Unknown,
                AggregateEffect = "unknown",
            };
            ReferenceCollector.AddSurface(incomplete, Array.Empty<AspxPaginationPageReceipt>(),
                new[] { incomplete.SurfaceId + ":folder_url_missing" });
            result = result with { Validation = result.Validation with { Outcome = DiscoveryTerminalOutcome.Unknown }, Surface = incomplete };
        }
        if (files && result.Items.Any(item => IsAspxRow(item) &&
            (!Guid.TryParse(PropertyString(item, "UniqueId"), out var id) || id == Guid.Empty)))
        {
            var incomplete = result.Surface with
            {
                TerminalOutcome = DiscoveryTerminalOutcome.Unknown,
                ExpectedCount = null,
                ExpectedCountState = AspxExpectedCountState.Unknown,
                AggregateEffect = "unknown",
            };
            ReferenceCollector.AddSurface(incomplete, Array.Empty<AspxPaginationPageReceipt>(),
                new[] { incomplete.SurfaceId + ":" + MissingFileIdentity });
            result = result with { Validation = result.Validation with { Outcome = DiscoveryTerminalOutcome.Unknown }, Surface = incomplete };
        }
        if (IsComplete(result.Validation.Outcome))
        {
            folderCollections[key] = result;
        }
        return result;
    }

    /// <summary>Recognizes ASPX physical rows solely by file name or the guaranteed folder-file locator.</summary>
    private static bool IsAspxRow(JsonElement item) => string.Equals(Path.GetExtension(
        PropertyString(item, "Name") ?? PropertyString(item, "ServerRelativeUrl")), ".aspx", StringComparison.OrdinalIgnoreCase);

    /// <summary>Identifies one physical folder within this provider's authorized Web and permission boundary, independently of its scope entrance.</summary>
    private static string FolderIdentity(LiveScope scope) => scope.WebUrl.GetLeftPart(UriPartial.Authority) +
        "|" + (AspxReferencePath.Normalize(scope.FolderUrl?.Replace("%20", " ", StringComparison.OrdinalIgnoreCase)) ??
            scope.FolderUrl ?? string.Empty).TrimEnd('/');

    /// <summary>Selects only observed list-root containment as physical ownership; a form/view declaration never authorizes an owner GUID.</summary>
    private LiveScope PhysicalOwner(LiveScope scope, string path) => listRoots
        .Where(root => !string.IsNullOrWhiteSpace(root.FolderUrl) &&
            (string.Equals(NormalizeServerRelativePath(path)?.TrimEnd('/'),
                NormalizeServerRelativePath(root.FolderUrl)?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ||
             NormalizeServerRelativePath(path)?.StartsWith(NormalizeServerRelativePath(root.FolderUrl).TrimEnd('/') + "/",
                StringComparison.OrdinalIgnoreCase) == true))
        .OrderByDescending(root => root.FolderUrl.Length).FirstOrDefault();

    /// <summary>Applies physical root ownership while preserving the original parent scope and the current folder's observed GUID.</summary>
    private LiveScope PhysicalScope(LiveScope scope)
    {
        var owner = PhysicalOwner(scope, scope.FolderUrl);
        return scope with
        {
            ListId = owner?.ListId,
            BaseType = owner?.BaseType,
            Metadata = WithMetadata(owner?.Metadata ?? scope.Metadata,
                ("folderUniqueId", observedFolderIds.GetValueOrDefault(FolderIdentity(scope)) ??
                    MetadataString(scope.Metadata, "folderUniqueId") ?? string.Empty)),
        };
    }

    /// <summary>Limits cross-entrance content reuse to terminal successful collection outcomes.</summary>
    private static bool IsComplete(DiscoveryTerminalOutcome outcome) =>
        outcome is DiscoveryTerminalOutcome.Complete or DiscoveryTerminalOutcome.Empty;

    /// <summary>Retains modeled physical-folder provenance and treats missing ASPX identities as incomplete authority rather than successful coverage.</summary>
    private void RecordModeledFolderSurface(LiveScope scope, SharePointModeledFolderResult result, bool files)
    {
        var observed = files ? result.Files.Count : result.Folders.Count;
        var identityGap = files && result.Files.Any(item => string.Equals(Path.GetExtension(item.Name ?? item.ServerRelativeUrl),
            ".aspx", StringComparison.OrdinalIgnoreCase) && (!Guid.TryParse(item.UniqueId, out var id) || id == Guid.Empty));
        var known = IsComplete(result.Outcome) && !identityGap;
        var outcome = identityGap ? DiscoveryTerminalOutcome.Unknown
            : known && observed == 0 ? DiscoveryTerminalOutcome.Empty : result.Outcome;
        var select = files ? "UniqueId,Name,ServerRelativeUrl,CustomizedPageStatus,Properties"
            : "UniqueId,Name,ServerRelativeUrl";
        var surfaceId = (files ? "web-root-files:" : "web-root-folders:") + scope.ScopeKey;
        var row = SurfaceRow(scope.ScopeKey, scope.ParentScopeKey, surfaceId, scope.FolderUrl,
            select, string.Empty, "PnPCoreModeledWebRootAuthority", AspxSurfaceApplicability.Applicable,
            outcome, known ? observed : null,
            known ? AspxExpectedCountState.Known : AspxExpectedCountState.Unknown, observed,
            DiscoveryHash.Of(result.Provider, result.Operation, scope.FolderUrl, files.ToString(),
                observed.ToString(System.Globalization.CultureInfo.InvariantCulture)), 0,
            new[] { result.EvidenceRef, "provider=" + result.Provider, "operation=" + result.Operation },
            files ? "PnPCoreWebRootFilesAuthority" : "PnPCoreWebRootFoldersAuthority",
            known && observed == 0 ? "AuthorityTerminalZero" : null,
            known && observed == 0 ? scope.FolderUrl : null) with
        {
            ActualMethod = "MODEL",
            ActualEndpoint = result.Provider + ":" + result.Operation,
            AsOfUtc = result.ReceivedAtUtc,
        };
        ReferenceCollector.AddSurface(row, Array.Empty<AspxPaginationPageReceipt>(), known
            ? Array.Empty<string>() : new[] { surfaceId + ":" + (identityGap ? MissingFileIdentity : result.ErrorCode ?? "modeled_authority_incomplete") });
    }

    private void RecordModeledValueSurface(LiveScope scope, string surfaceId, SharePointModeledValue result,
        DiscoveryTerminalOutcome outcome, AspxSurfaceApplicability applicability, string adapter)
    {
        var known = outcome is DiscoveryTerminalOutcome.Complete or DiscoveryTerminalOutcome.Empty;
        var observed = string.IsNullOrWhiteSpace(result.Value) ? 0 : 1;
        var row = SurfaceRow(scope.ScopeKey, scope.ParentScopeKey, surfaceId, scope.WebUrl.AbsoluteUri,
            "WelcomePage", string.Empty, "PnPCoreModeledWelcomePageAuthority", applicability, outcome,
            known ? observed : null, known ? AspxExpectedCountState.Known : AspxExpectedCountState.Unknown,
            observed, DiscoveryHash.Of(result.Provider, result.Operation, result.Value ?? string.Empty), 0,
            new[] { result.EvidenceRef, "provider=" + result.Provider, "operation=" + result.Operation },
            adapter, known && observed == 0 ? "AuthorityTerminalZero" : null,
            known && observed == 0 ? scope.WebUrl.AbsoluteUri : null) with
        {
            ActualMethod = "MODEL",
            ActualEndpoint = result.Provider + ":" + result.Operation,
            AsOfUtc = result.ReceivedAtUtc,
        };
        ReferenceCollector.AddSurface(row, Array.Empty<AspxPaginationPageReceipt>(), known
            ? Array.Empty<string>() : new[] { surfaceId + ":" + (result.ErrorCode ?? "modeled_authority_incomplete") });
    }

    /// <summary>Retains paginated authority receipts and terminal failures, including partial evidence before cancellation.</summary>
    private async Task<LiveCollectionResult> ReadCollectionAsync(string scopeKey, string parentScopeKey,
        string surfaceId, Uri initialEndpoint, string select, string filter,
        AspxSurfaceApplicability applicability, string adapter, CancellationToken cancellationToken,
        AspxSurfaceDispositionRule dispositionRule = null)
    {
        var client = await clientFactory.GetAsync(WebUrlFromEndpoint(initialEndpoint), cancellationToken)
            .ConfigureAwait(false);
        var pages = new List<LivePage>();
        var items = new List<JsonElement>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Uri next = initialEndpoint;
        string requestToken = null;
        var providerOutcome = DiscoveryTerminalOutcome.Complete;
        var cancelled = false;
        for (var ordinal = 0; next != null; ordinal++)
        {
            SharePointRestPage page;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                page = await client.GetPageAsync(next, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                providerOutcome = DiscoveryTerminalOutcome.Cancelled;
                cancelled = true;
                break;
            }
            catch (Exception ex)
            {
                page = new SharePointRestPage(next, 0, Array.Empty<JsonElement>(), null,
                    DiscoveryHash.Of(ex.GetType().FullName, ex.Message), "transport-error",
                    DiscoveryTerminalOutcome.Failed, "transport_failure",
                    SharePointSemanticDetectorResults.None, DateTimeOffset.UtcNow,
                    AttemptCount: 1, AttemptLimit: 1);
            }
            var nextLink = page.NextLink;
            var terminal = string.IsNullOrWhiteSpace(nextLink) || page.Outcome != DiscoveryTerminalOutcome.Complete;
            var nextUri = terminal ? null : ResolveNext(initialEndpoint, nextLink);
            var receipt = new AspxPaginationPageReceipt(AspxAcquisitionVersions.PaginationReceipt,
                surfaceId, options.AuthorityRevision,
                DiscoveryHash.Of(initialEndpoint.GetLeftPart(UriPartial.Path), select, filter ?? string.Empty),
                "GET", AspxDurableRequestEvidence.Endpoint(page.RequestUri), select, filter ?? string.Empty, ordinal,
                AspxPaginationContract.TokenHash(requestToken), page.Items.Count,
                AspxPaginationContract.TokenHash(nextLink), page.ResponseDigest,
                page.StatusCode == 0 ? null : (int)page.StatusCode, page.SemanticDetectorResult,
                page.AttemptCount, page.AttemptLimit, page.RequestId, page.CorrelationId, page.ErrorCode,
                terminal, page.ReceivedAtUtc == default ? DateTimeOffset.UtcNow : page.ReceivedAtUtc);
            pages.Add(new LivePage(page, receipt));
            items.AddRange(page.Items);
            if (page.Outcome != DiscoveryTerminalOutcome.Complete)
            {
                providerOutcome = page.Outcome;
                next = null;
            }
            else if (nextUri == null)
            {
                next = null;
            }
            else if (!seen.Add(nextLink))
            {
                providerOutcome = DiscoveryTerminalOutcome.Unknown;
                ReferenceCollector.AddGap(surfaceId + ":pagination_token_loop_or_loss");
                next = null;
            }
            else
            {
                requestToken = nextLink;
                next = nextUri;
            }
        }

        var validation = AspxPaginationContract.Validate(pages.Select(page => page.Receipt).ToArray(), providerOutcome);
        if (cancelled) validation = validation with { Outcome = DiscoveryTerminalOutcome.Cancelled };
        var expectedState = validation.Outcome is DiscoveryTerminalOutcome.Complete or DiscoveryTerminalOutcome.Empty
            ? AspxExpectedCountState.Known : AspxExpectedCountState.Unknown;
        int? expected = expectedState == AspxExpectedCountState.Known ? items.Count : null;
        var outcome = validation.Outcome == DiscoveryTerminalOutcome.Complete && items.Count == 0
            ? DiscoveryTerminalOutcome.Empty : validation.Outcome;
        var appliedDispositionRule = dispositionRule != null && pages.Any(page => dispositionRule.Applies(page.Page))
            ? dispositionRule : null;
        var absenceKind = outcome == DiscoveryTerminalOutcome.Empty ? "AuthorityTerminalZero" : null;
        var row = SurfaceRow(scopeKey, parentScopeKey, surfaceId, initialEndpoint.AbsoluteUri, select,
            filter ?? string.Empty, "SharePointRestCollectionAuthority", applicability, outcome,
            expected, expectedState, items.Count, validation.ChainHash, validation.OutstandingTokenCount,
            pages.Select(page => EvidenceRef(initialEndpoint, page.Page, select)).ToArray(), adapter,
            absenceKind, absenceKind == null ? null : initialEndpoint.AbsoluteUri, appliedDispositionRule);
        ReferenceCollector.AddSurface(row, pages.Select(page => page.Receipt).ToArray(),
            validation.GapCodes.Select(code => surfaceId + ":" + code).ToArray());
        if (cancelled) throw new OperationCanceledException(cancellationToken);
        return new(items, pages, validation with { Outcome = outcome }, appliedDispositionRule, row);
    }

    private AspxSurfaceDenominatorRow SurfaceRow(string scopeKey, string parentScopeKey, string surfaceId,
        string endpoint, string select, string filter, string authorityKind,
        AspxSurfaceApplicability applicability, DiscoveryTerminalOutcome outcome, int? expected,
        AspxExpectedCountState expectedState, int observed, string chainHash, int outstanding,
        IReadOnlyList<string> evidenceRefs, string adapter, string absenceKind = null, string absenceRef = null,
        AspxSurfaceDispositionRule dispositionRule = null) =>
        new(scopeKey, parentScopeKey, surfaceId, applicability,
            dispositionRule?.RuleId, dispositionRule?.RuleVersion,
            dispositionRule?.RuleHash, dispositionRule?.ReviewRef, dispositionRule?.PlatformBinding,
            applicability == AspxSurfaceApplicability.NotApplicable
                ? AspxRuntimeCounterexampleState.Observed : AspxRuntimeCounterexampleState.NoneObserved,
            authorityKind, endpoint, options.AuthorityRevision, options.AuthorityHash,
            "GET", endpoint, select, filter, options.VisibilityBoundary, options.PermissionContext,
            expected, expectedState, observed, outcome,
            outcome is DiscoveryTerminalOutcome.Complete or DiscoveryTerminalOutcome.Empty ? "satisfied"
                : outcome is DiscoveryTerminalOutcome.Denied or DiscoveryTerminalOutcome.Failed or
                    DiscoveryTerminalOutcome.Truncated or DiscoveryTerminalOutcome.Cancelled ? "incomplete" : "unknown",
            outstanding != 0, chainHash, outstanding, absenceKind, absenceRef, DateTimeOffset.UtcNow,
            (evidenceRefs ?? Array.Empty<string>()).Concat(dispositionRule?.EvidenceRefs ?? Array.Empty<string>())
                .Distinct(StringComparer.Ordinal).ToArray(), adapter,
            dispositionRule?.ClassificationEffect ??
                (applicability == AspxSurfaceApplicability.Applicable ? "physical-and-reference"
                    : applicability == AspxSurfaceApplicability.SystemOrVirtualOnly ? "reference-first" : "fail-closed"));

    private static RawDiscoveryBatch ToRawBatch(LivePage page, IReadOnlyList<RawDiscoveryRecord> records,
        DiscoveryTerminalOutcome finalOutcome)
    {
        var terminal = page.Receipt.TerminalFlag;
        var outcome = terminal ? finalOutcome : DiscoveryTerminalOutcome.Pending;
        return new(page.Receipt.PageOrdinal,
            DiscoveryHash.Of(page.Receipt.ActualEndpointHash, page.Receipt.RequestTokenHash),
            page.Receipt.ResponseDigest, records, terminal, outcome, page.Receipt.NextTokenHash,
            outcome == DiscoveryTerminalOutcome.Unknown ? DiscoveryGapCodes.PaginationTokenLoopOrLoss : null,
            outcome == DiscoveryTerminalOutcome.Unknown ? "Live pagination receipt failed v3 integrity validation." : null);
    }

    private AspxReferenceCandidate Candidate(string sourceKind, string objectId, string method, string locator,
        string disposition, string reason, string fileUniqueId, string contentOrigin,
        params string[] evidence) => new(sourceKind, objectId, method, null, locator,
            AspxReferencePath.Normalize(locator), null, disposition, reason, fileUniqueId,
            contentOrigin, options.PermissionContext,
            evidence.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray());

    private static Dictionary<string, string> EvidenceMetadata(Uri endpoint, string select, string filter,
        string schemaFlavor, params (string Key, string Value)[] additional)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["actualMethod"] = "GET",
            ["actualEndpoint"] = endpoint.AbsoluteUri,
            ["actualSelect"] = select ?? string.Empty,
            ["actualFilter"] = filter ?? string.Empty,
            ["responseSchemaFlavor"] = schemaFlavor ?? "unknown",
        };
        foreach (var (key, value) in additional) metadata[key] = value ?? string.Empty;
        return metadata;
    }

    private string EvidenceRef(Uri endpoint, SharePointRestPage page, string select) =>
        AspxDurableRequestEvidence.Reference(endpoint, page, select);

    private static DiscoveryChildEnumerationResult Result(LiveScope parent,
        IReadOnlyList<LiveScope> children, DiscoveryTerminalOutcome outcome, string gapCode = null)
    {
        var expected = children.Select(child => new DiscoveryChildExpectation(child.ScopeKey, child.Kind,
            child.SourceKind, child.Locator, child.PermissionContext, Required: true)).ToArray();
        return new(AspxDiscoveryHierarchy.ChildKindFor(parent.Kind), expected,
            children.Select(Registration).ToArray(), outcome, parent.PermissionContext, gapCode,
            gapCode == null ? null : $"Live acquisition retained '{parent.ScopeKey}' and failed closed: {gapCode}.");
    }

    private static DiscoveryScopeRegistration Registration(LiveScope scope) => new(
        scope.ScopeKey, scope.ParentScopeKey, scope.Kind, scope.SourceKind, scope.Locator,
        scope.PermissionContext, Required: true, Metadata: WithMetadata(scope.Metadata,
            ("listId", scope.ListId?.ToString("D") ?? string.Empty), ("role", scope.Role)));

    private LiveScope Add(LiveScope scope)
    {
        scopes.TryAdd(scope.ScopeKey, scope);
        return scopes[scope.ScopeKey];
    }

    private string Key(params string[] values) => DiscoveryHash.Of(values)[..24];
    private static Uri Endpoint(Uri webUrl, string relative) => new(webUrl.AbsoluteUri.TrimEnd('/') + "/" + relative.TrimStart('/'));
    private static Uri FolderEndpoint(LiveScope scope, string child, string select, string expand = null)
    {
        var query = $"$select={select}" + (string.IsNullOrWhiteSpace(expand) ? string.Empty : $"&$expand={expand}");
        if (scope.Role == "web-root-folder")
            return Endpoint(scope.WebUrl, $"_api/web/RootFolder/{child}?{query}");
        // Keep decoded ResourcePath values unambiguous inside the HTTP URI. In particular, a raw '#'
        // would otherwise start the URI fragment and silently truncate the requested folder and query.
        var decoded = (scope.FolderUrl ?? string.Empty).Replace("%20", " ", StringComparison.Ordinal)
            .Replace('\\', '/').Replace("'", "''", StringComparison.Ordinal);
        var encoded = WebUtility.UrlEncode(decoded).Replace("+", "%20", StringComparison.Ordinal);
        return Endpoint(scope.WebUrl,
            $"_api/web/GetFolderByServerRelativePath(decodedUrl=@u)/{child}?@u='{encoded}'&{query}");
    }
    private static Uri ResolveNext(Uri initial, string next) =>
        Uri.TryCreate(next, UriKind.Absolute, out var absolute) ? absolute : new Uri(initial, next);
    private static Uri WebUrlFromEndpoint(Uri endpoint)
    {
        var marker = endpoint.AbsoluteUri.IndexOf("/_api/", StringComparison.OrdinalIgnoreCase);
        return marker > 0 ? new Uri(endpoint.AbsoluteUri[..marker]) : new Uri(endpoint.GetLeftPart(UriPartial.Authority));
    }
    private static string PropertyString(JsonElement item, string name) =>
        PnPContextSharePointAspxRestClient.TryProperty(item, name, out var value) &&
        value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? value.ToString() : null;
    /// <summary>Reads optional numeric classification metadata without rejecting null or malformed BaseType values as list-admission failures.</summary>
    private static int? PropertyInt(JsonElement item, string name) =>
        PnPContextSharePointAspxRestClient.TryProperty(item, name, out var value) &&
        ((value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) ||
            int.TryParse(value.ToString(), out number)) ? number : null;
    private static bool? PropertyBool(JsonElement item, string name) =>
        PnPContextSharePointAspxRestClient.TryProperty(item, name, out var value) &&
        (value.ValueKind is JsonValueKind.True or JsonValueKind.False || bool.TryParse(value.ToString(), out _))
            ? value.ValueKind == JsonValueKind.True || bool.Parse(value.ToString()) : null;
    /// <summary>Accepts only observed nonempty GUIDs for list authority identities.</summary>
    private static Guid? PropertyGuid(JsonElement item, string name) =>
        Guid.TryParse(PropertyString(item, name), out var value) && value != Guid.Empty ? value : null;
    /// <summary>Reads registered display-form declarations from the list metadata row.</summary>
    private static ListDisplayForms ReadDisplayForms(JsonElement item)
    {
        var contentTypeForms = NestedArray(item, "ContentTypes")
            .Select(contentType => new ContentTypeForm(ReadContentTypeId(contentType),
                PropertyString(contentType, "DisplayFormUrl")))
            .Where(form => !string.IsNullOrWhiteSpace(form.ContentTypeId) &&
                !string.IsNullOrWhiteSpace(form.FormUrl))
            .OrderBy(form => form.ContentTypeId, StringComparer.Ordinal)
            .ToArray();
        return new ListDisplayForms(PropertyString(item, "DefaultDisplayFormUrl"), contentTypeForms);
    }

    private static string ReadContentTypeId(JsonElement contentType)
    {
        if (!PnPContextSharePointAspxRestClient.TryProperty(contentType, "Id", out var id)) return null;
        // REST exposes SP.ContentTypeId as { StringValue: "0x..." }; it is not the list item's
        // string-valued ContentTypeId field. Retain support for already-flattened identifiers.
        if (id.ValueKind == JsonValueKind.Object)
            return PnPContextSharePointAspxRestClient.TryProperty(id, "StringValue", out var value) &&
                value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return id.ValueKind == JsonValueKind.String ? id.GetString() : null;
    }

    // A $expand'ed collection arrives either as a bare array or wrapped in a "results" array,
    // depending on the OData flavour the response was negotiated with.
    private static IEnumerable<JsonElement> NestedArray(JsonElement item, string name)
    {
        if (!PnPContextSharePointAspxRestClient.TryProperty(item, name, out var nested))
            return Array.Empty<JsonElement>();
        if (nested.ValueKind == JsonValueKind.Object &&
            PnPContextSharePointAspxRestClient.TryProperty(nested, "results", out var results))
            nested = results;
        return nested.ValueKind == JsonValueKind.Array
            ? nested.EnumerateArray().ToArray() : Array.Empty<JsonElement>();
    }

    private static string NestedString(JsonElement item, string parent, string child) =>
        PnPContextSharePointAspxRestClient.TryProperty(item, parent, out var nested) ? PropertyString(nested, child) : null;
    private static int? NestedInt(JsonElement item, string parent, string child) =>
        PnPContextSharePointAspxRestClient.TryProperty(item, parent, out var nested) ? PropertyInt(nested, child) : null;
    /// <summary>Preserves unknown identity as null rather than claiming Guid.Empty as an observed site, Web or folder GUID.</summary>
    private static Guid? MetadataGuid(IReadOnlyDictionary<string, string> metadata, string key) =>
        metadata != null && metadata.TryGetValue(key, out var value) && Guid.TryParse(value, out var parsed) &&
            parsed != Guid.Empty ? parsed : null;
    private static string MetadataString(IReadOnlyDictionary<string, string> metadata, string key) =>
        metadata != null && metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
    private static bool? MetadataBool(IReadOnlyDictionary<string, string> metadata, string key) =>
        metadata != null && metadata.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) ? parsed : null;
    private static string InferPageType(string contentTypeId)
    {
        if (string.IsNullOrWhiteSpace(contentTypeId)) return null;
        if (contentTypeId.StartsWith("0x0101009D1CB255DA76424F860D91F20E6C4118", StringComparison.OrdinalIgnoreCase))
            return "ModernSitePage";
        if (IsPublishingPageContentType(contentTypeId))
            return "PublishingPage";
        if (contentTypeId.StartsWith("0x01010901", StringComparison.OrdinalIgnoreCase)) return "WebPartPage";
        if (contentTypeId.StartsWith("0x010109", StringComparison.OrdinalIgnoreCase)) return "BasicPage";
        if (contentTypeId.StartsWith("0x010108", StringComparison.OrdinalIgnoreCase)) return "WikiPage";
        if (contentTypeId.StartsWith("0x010105", StringComparison.OrdinalIgnoreCase)) return "MasterPage";
        return "OtherAspxContentType";
    }

    internal static bool IsPublishingPageContentType(string contentTypeId) => !string.IsNullOrWhiteSpace(contentTypeId) &&
        (contentTypeId.StartsWith("0x010100C568DB52D9D0A14D9B2FDCC96666E9F2007948130EC3DB064584E219954237AF39", StringComparison.OrdinalIgnoreCase) ||
         contentTypeId.StartsWith("0x01010007FF3E057FA8AB4AA42FCB67B453FFC1", StringComparison.OrdinalIgnoreCase));
    private static IReadOnlyDictionary<string, string> WithMetadata(
        IReadOnlyDictionary<string, string> metadata, params (string Key, string Value)[] additions)
    {
        var copy = metadata == null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(metadata, StringComparer.Ordinal);
        foreach (var (key, value) in additions) copy[key] = value ?? string.Empty;
        return copy;
    }
    private bool? IsHomePage(LiveScope scope, string serverRelativeUrl)
    {
        if (scope.WebUrl == null || !welcomePageByWeb.TryGetValue(scope.WebUrl.AbsoluteUri.TrimEnd('/'), out var welcomePage))
            return null;
        return string.Equals(welcomePage, NormalizeServerRelativePath(serverRelativeUrl), StringComparison.OrdinalIgnoreCase);
    }
    private string HomePageStatus(LiveScope scope, string serverRelativeUrl) => IsHomePage(scope, serverRelativeUrl) switch
    {
        true => "matched",
        false => "not-matched",
        null => "unknown",
    };
    private static string NormalizeServerRelativePath(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : "/" + value.Replace('\\', '/').Trim().TrimStart('/');
    private static DiscoveryTerminalOutcome MergeOutcome(DiscoveryTerminalOutcome current,
        DiscoveryTerminalOutcome next)
    {
        if (current == DiscoveryTerminalOutcome.Unknown || next == DiscoveryTerminalOutcome.Unknown)
            return DiscoveryTerminalOutcome.Unknown;
        if (current is DiscoveryTerminalOutcome.Denied or DiscoveryTerminalOutcome.Failed or DiscoveryTerminalOutcome.Truncated ||
            next is DiscoveryTerminalOutcome.Denied or DiscoveryTerminalOutcome.Failed or DiscoveryTerminalOutcome.Truncated)
            return next is DiscoveryTerminalOutcome.Denied or DiscoveryTerminalOutcome.Failed or DiscoveryTerminalOutcome.Truncated
                ? next : current;
        return current;
    }

    private sealed record LiveScope(
        string ScopeKey,
        string ParentScopeKey,
        DiscoveryScopeKind Kind,
        DiscoverySourceKind? SourceKind,
        string Locator,
        string Role,
        Uri WebUrl,
        Guid? ListId,
        int? BaseType,
        string FolderUrl,
        IReadOnlyDictionary<string, string> Metadata)
    {
        internal string PermissionContext => "live:" + Role;
    }

    private sealed record LivePage(SharePointRestPage Page, AspxPaginationPageReceipt Receipt);
    private sealed record ContentTypeForm(string ContentTypeId, string FormUrl);
    private sealed record ListDisplayForms(string DefaultFormUrl,
        IReadOnlyList<ContentTypeForm> ContentTypeForms);
    private sealed record WelcomePageAcquisition(
        DiscoveryTerminalOutcome Outcome,
        string Locator,
        RawDiscoveryRecord Physical,
        string ErrorCode);
    private sealed record LiveCollectionResult(
        IReadOnlyList<JsonElement> Items,
        IReadOnlyList<LivePage> Pages,
        AspxPaginationValidation Validation,
        AspxSurfaceDispositionRule AppliedDispositionRule,
        AspxSurfaceDenominatorRow Surface);
}
