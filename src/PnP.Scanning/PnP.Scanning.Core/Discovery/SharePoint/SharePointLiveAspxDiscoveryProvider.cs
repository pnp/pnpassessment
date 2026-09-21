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
    private const string AllListsSelect = "Id,Title,BaseType,BaseTemplate,Hidden,IsCatalog,RootFolder/ServerRelativeUrl,RootFolder/UniqueId,DefaultViewUrl";
    private const string FormsSelect = "Id,ServerRelativeUrl,FormType";
    private const string ViewsSelect = "Id,ServerRelativeUrl,Hidden,DefaultView,PersonalView,Title";
    private const string FilesSelect = "UniqueId,Name,ServerRelativeUrl,CustomizedPageStatus,ListItemAllFields/Id,ListItemAllFields/ContentTypeId,Length";
    private const string FoldersSelect = "UniqueId,Name,ServerRelativeUrl";
    private readonly SharePointLiveAspxDiscoveryOptions options;
    private readonly ISharePointAspxRestClientFactory clientFactory;
    private readonly Dictionary<string, LiveScope> scopes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SharePointModeledFolderResult> modeledFolders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RawDiscoveryRecord> targetedFiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> welcomePageByWeb = new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;

    internal SharePointLiveAspxDiscoveryProvider(SharePointLiveAspxDiscoveryOptions options,
        ISharePointAspxRestClientFactory clientFactory, AspxWebAcquisitionContext web)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        ArgumentNullException.ThrowIfNull(web);
        if (!web.SiteUrl.IsAbsoluteUri || web.SiteUrl.Scheme != Uri.UriSchemeHttps ||
            !web.WebUrl.IsAbsoluteUri || web.WebUrl.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Site and Web URLs must be absolute HTTPS URLs.", nameof(web));
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

    private async Task<DiscoveryChildEnumerationResult> EnumerateWebAsync(LiveScope parent,
        CancellationToken cancellationToken)
    {
        var welcomePage = await AcquireWelcomePageAsync(parent, cancellationToken).ConfigureAwait(false);
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

        var endpoint = Endpoint(parent.WebUrl,
            $"_api/web/lists?$select={AllListsSelect}&$expand=RootFolder");
        var listResult = await ReadCollectionAsync(parent.ScopeKey, parent.ScopeKey,
            "lists:" + parent.ScopeKey, endpoint, AllListsSelect, string.Empty,
            AspxSurfaceApplicability.Applicable, "AllListsAuthority", cancellationToken).ConfigureAwait(false);
        var containers = new List<LiveScope>
        {
            Add(new LiveScope(Key("web-root", parent.WebUrl.AbsoluteUri), parent.ScopeKey,
                DiscoveryScopeKind.Container, null, parent.WebUrl.AbsoluteUri, "web-root", parent.WebUrl,
                null, null, null, parent.Metadata)),
        };
        foreach (var item in listResult.Items)
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
            containers.Add(scope);
            RecordListApplicability(parent, scope, template);
        }
        return Result(parent, containers, listResult.Validation.Outcome);
    }

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
        if (decision.RawLibraryRequired && !string.IsNullOrWhiteSpace(parent.FolderUrl))
            children.Add(Add(new LiveScope(Key("folder", parent.WebUrl.AbsoluteUri, parent.FolderUrl), parent.ScopeKey,
                DiscoveryScopeKind.Folder, DiscoverySourceKind.RawListLibraryFiles, parent.FolderUrl,
                "folder", parent.WebUrl, parent.ListId, parent.BaseType, parent.FolderUrl, parent.Metadata)));
        if (!string.IsNullOrWhiteSpace(parent.FolderUrl))
        {
            var formsFolder = parent.FolderUrl.TrimEnd('/') + "/Forms";
            children.Add(Add(new LiveScope(Key("physical-forms", parent.WebUrl.AbsoluteUri, formsFolder),
                parent.ScopeKey, DiscoveryScopeKind.Folder, DiscoverySourceKind.RawListLibraryFiles,
                formsFolder, "physical-forms", parent.WebUrl, parent.ListId, parent.BaseType,
                formsFolder, parent.Metadata)));
        }
        children.Add(Add(new LiveScope(Key("forms", parent.WebUrl.AbsoluteUri, parent.ListId?.ToString("D") ?? parent.ScopeKey),
            parent.ScopeKey, DiscoveryScopeKind.Folder, DiscoverySourceKind.ListFormBackingFiles,
            parent.Locator + "/Forms REST objects", "forms", parent.WebUrl, parent.ListId,
            parent.BaseType, parent.FolderUrl, parent.Metadata)));
        children.Add(Add(new LiveScope(Key("views", parent.WebUrl.AbsoluteUri, parent.ListId?.ToString("D") ?? parent.ScopeKey),
            parent.ScopeKey, DiscoveryScopeKind.Folder, DiscoverySourceKind.ListViewBackingFiles,
            parent.Locator + "/Views REST objects", "views", parent.WebUrl, parent.ListId,
            parent.BaseType, parent.FolderUrl, parent.Metadata)));

        var outcome = decision.Outcome;
        return Task.FromResult(Result(parent, children, outcome,
            outcome == DiscoveryTerminalOutcome.Unknown ? "invalid_or_unspecified_base_type" : null));
    }

    private async Task<DiscoveryChildEnumerationResult> EnumerateFolderAsync(LiveScope parent,
        CancellationToken cancellationToken)
    {
        if (parent.Role is "forms" or "views" or "welcome-page")
            return Result(parent, Array.Empty<LiveScope>(), DiscoveryTerminalOutcome.Empty);
        if (parent.SourceKind == DiscoverySourceKind.WebRootFiles)
        {
            var modeledResult = await ReadModeledFolderAsync(parent, cancellationToken).ConfigureAwait(false);
            RecordModeledFolderSurface(parent, modeledResult, files: false);
            if (modeledResult.Outcome is DiscoveryTerminalOutcome.Complete or DiscoveryTerminalOutcome.Empty)
            {
                var modeledChildren = modeledResult.Folders.Select(item => Add(new LiveScope(
                    Key("web-root-folder", parent.WebUrl.AbsoluteUri, item.ServerRelativeUrl), parent.ScopeKey,
                    DiscoveryScopeKind.Folder, DiscoverySourceKind.WebRootFiles, item.ServerRelativeUrl,
                    "folder", parent.WebUrl, null, null, item.ServerRelativeUrl,
                    WithMetadata(parent.Metadata, ("folderUniqueId", item.UniqueId))))).ToArray();
                return Result(parent, modeledChildren, modeledResult.Outcome, modeledResult.ErrorCode);
            }
        }
        var endpoint = FolderEndpoint(parent, "Folders", FoldersSelect);
        var result = await ReadCollectionAsync(parent.ScopeKey, parent.ScopeKey,
            "folders:" + parent.ScopeKey, endpoint, FoldersSelect, string.Empty,
            AspxSurfaceApplicability.Applicable, "RecursiveFolderAuthority", cancellationToken).ConfigureAwait(false);
        var children = result.Items.Select(item =>
        {
            var folderUrl = PropertyString(item, "ServerRelativeUrl");
            return Add(new LiveScope(Key("folder", parent.WebUrl.AbsoluteUri, folderUrl ?? Guid.NewGuid().ToString("N")),
                parent.ScopeKey, DiscoveryScopeKind.Folder, parent.SourceKind, folderUrl ?? "missing-folder-url",
                "folder", parent.WebUrl, parent.ListId, parent.BaseType, folderUrl,
                WithMetadata(parent.Metadata, ("folderUniqueId", PropertyString(item, "UniqueId") ?? string.Empty))));
        }).ToArray();
        return Result(parent, children, result.Validation.Outcome);
    }

    private async IAsyncEnumerable<RawDiscoveryBatch> ReadFolderFilesAsync(LiveScope scope,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (scope.SourceKind == DiscoverySourceKind.WebRootFiles)
        {
            var modeled = await ReadModeledFolderAsync(scope, cancellationToken).ConfigureAwait(false);
            RecordModeledFolderSurface(scope, modeled, files: true);
            if (modeled.Outcome is DiscoveryTerminalOutcome.Complete or DiscoveryTerminalOutcome.Empty)
            {
                var records = modeled.Files.Select(item => new RawDiscoveryRecord(
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
                        (Guid.TryParse(modeled.FolderUniqueId, out var modeledFolderId) ? modeledFolderId : null),
                    HomePage: IsHomePage(scope, item.ServerRelativeUrl),
                    LibraryHidden: MetadataBool(scope.Metadata, "hidden"),
                    CustomizedPageStatusRaw: item.CustomizedPageStatus,
                    ObservationMethod: modeled.Provider + ":" + modeled.Operation,
                    WelcomePageStatus: HomePageStatus(scope, item.ServerRelativeUrl),
                    SiteUrl: MetadataString(scope.Metadata, "siteUrl") ?? scope.WebUrl?.GetLeftPart(UriPartial.Authority),
                    WebUrl: scope.WebUrl?.AbsoluteUri)).ToArray();
                var terminalOutcome = modeled.Outcome == DiscoveryTerminalOutcome.Complete && records.Length == 0
                    ? DiscoveryTerminalOutcome.Empty : modeled.Outcome;
                yield return new RawDiscoveryBatch(0,
                    DiscoveryHash.Of(modeled.Provider, modeled.Operation, scope.FolderUrl),
                    DiscoveryHash.Of(string.Join('|', records.Select(item => item.FileUniqueId))), records,
                    IsTerminal: true, terminalOutcome);
                yield break;
            }
        }
        var endpoint = FolderEndpoint(scope, "Files", FilesSelect, "ListItemAllFields");
        var result = await ReadCollectionAsync(scope.ScopeKey, scope.ParentScopeKey,
            "files:" + scope.ScopeKey, endpoint, FilesSelect, string.Empty,
            AspxSurfaceApplicability.Applicable, scope.Role == "physical-forms"
                ? "PhysicalFormsTreeAuthority" : "DocumentLibraryFilesAuthority", cancellationToken).ConfigureAwait(false);
        foreach (var page in result.Pages)
        {
            var records = page.Page.Items.Select(item => new RawDiscoveryRecord(
                PropertyString(item, "UniqueId"), PropertyString(item, "UniqueId"),
                scope.ListId?.ToString("D") ?? scope.ScopeKey, PropertyString(item, "Name"),
                PropertyString(item, "ServerRelativeUrl"), true, options.PermissionContext,
                EvidenceMetadata(endpoint, FilesSelect, string.Empty, page.Page.SchemaFlavor,
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
                ObservationMethod: "SharePoint REST folder files",
                WelcomePageStatus: HomePageStatus(scope, PropertyString(item, "ServerRelativeUrl")),
                SiteUrl: MetadataString(scope.Metadata, "siteUrl") ?? scope.WebUrl?.GetLeftPart(UriPartial.Authority),
                WebUrl: scope.WebUrl?.AbsoluteUri))
                .ToArray();
            yield return ToRawBatch(page, records, result.Validation.Outcome);
        }
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
            var records = new List<RawDiscoveryRecord>();
            foreach (var item in page.Page.Items)
            {
                var objectId = PropertyString(item, "Id") ?? DiscoveryHash.Of(item.GetRawText());
                var locator = PropertyString(item, "ServerRelativeUrl");
                var candidate = await ResolveReferenceAsync(scope, isForm, objectId, locator,
                    EvidenceRef(endpoint, page.Page, select), cancellationToken).ConfigureAwait(false);
                ReferenceCollector.AddReference(candidate.Candidate);
                if (candidate.Physical != null) records.Add(candidate.Physical);
            }
            yield return ToRawBatch(page, records, result.Validation.Outcome);
        }
    }

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
        if (resolved.Outcome != DiscoveryTerminalOutcome.Complete || string.IsNullOrWhiteSpace(resolved.FileUniqueId))
        {
            var disposition = resolved.Outcome is DiscoveryTerminalOutcome.Denied or DiscoveryTerminalOutcome.Failed
                ? AspxReferenceDispositions.ReferenceUnavailable : AspxReferenceDispositions.Unknown;
            return (Candidate(sourceKind, sourceObjectIdentity, method, locator, disposition,
                resolved.ErrorCode, null, "unavailable", evidenceRef, resolved.EvidenceRef), null, resolved.Outcome);
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
            "Applicability is selected from actual BaseType; template/catalog flags remain explicit provenance.",
        };
        ReferenceCollector.AddSurface(SurfaceRow(web.ScopeKey, web.ScopeKey,
            "list-applicability:" + list.ScopeKey, list.Locator, string.Empty, string.Empty,
            "ActualListBaseTypeAuthority", applicability, outcome,
            expectedState == AspxExpectedCountState.Known ? 1 : null, expectedState,
            1, DiscoveryHash.Of("list-applicability", list.ScopeKey), 0, evidence,
            applicability == AspxSurfaceApplicability.Applicable ? "raw-files+physical-forms+forms+views"
                : "forms+views"), Array.Empty<AspxPaginationPageReceipt>(),
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

    private async Task<SharePointModeledFolderResult> ReadModeledFolderAsync(LiveScope scope,
        CancellationToken cancellationToken)
    {
        var key = scope.WebUrl.AbsoluteUri.TrimEnd('/').ToLowerInvariant() + "|" + scope.FolderUrl;
        if (modeledFolders.TryGetValue(key, out var existing)) return existing;
        var client = await clientFactory.GetAsync(scope.WebUrl, cancellationToken).ConfigureAwait(false);
        var result = await client.ReadFolderAsync(scope.FolderUrl, cancellationToken).ConfigureAwait(false);
        modeledFolders[key] = result;
        return result;
    }

    private void RecordModeledFolderSurface(LiveScope scope, SharePointModeledFolderResult result, bool files)
    {
        var observed = files ? result.Files.Count : result.Folders.Count;
        var known = result.Outcome is DiscoveryTerminalOutcome.Complete or DiscoveryTerminalOutcome.Empty;
        var outcome = known && observed == 0 ? DiscoveryTerminalOutcome.Empty : result.Outcome;
        var select = files ? "UniqueId,Name,ServerRelativeUrl,CustomizedPageStatus"
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
            ? Array.Empty<string>() : new[] { surfaceId + ":" + (result.ErrorCode ?? "modeled_authority_incomplete") });
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
        for (var ordinal = 0; next != null; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
        return new(items, pages, validation with { Outcome = outcome }, appliedDispositionRule);
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
    private static int? PropertyInt(JsonElement item, string name) =>
        PnPContextSharePointAspxRestClient.TryProperty(item, name, out var value) &&
        (value.TryGetInt32(out var number) || int.TryParse(value.ToString(), out number)) ? number : null;
    private static bool? PropertyBool(JsonElement item, string name) =>
        PnPContextSharePointAspxRestClient.TryProperty(item, name, out var value) &&
        (value.ValueKind is JsonValueKind.True or JsonValueKind.False || bool.TryParse(value.ToString(), out _))
            ? value.ValueKind == JsonValueKind.True || bool.Parse(value.ToString()) : null;
    private static Guid? PropertyGuid(JsonElement item, string name) =>
        Guid.TryParse(PropertyString(item, name), out var value) ? value : null;
    private static string NestedString(JsonElement item, string parent, string child) =>
        PnPContextSharePointAspxRestClient.TryProperty(item, parent, out var nested) ? PropertyString(nested, child) : null;
    private static int? NestedInt(JsonElement item, string parent, string child) =>
        PnPContextSharePointAspxRestClient.TryProperty(item, parent, out var nested) ? PropertyInt(nested, child) : null;
    private static Guid? MetadataGuid(IReadOnlyDictionary<string, string> metadata, string key) =>
        metadata != null && metadata.TryGetValue(key, out var value) && Guid.TryParse(value, out var parsed) ? parsed : null;
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
    private sealed record WelcomePageAcquisition(
        DiscoveryTerminalOutcome Outcome,
        string Locator,
        RawDiscoveryRecord Physical,
        string ErrorCode);
    private sealed record LiveCollectionResult(
        IReadOnlyList<JsonElement> Items,
        IReadOnlyList<LivePage> Pages,
        AspxPaginationValidation Validation,
        AspxSurfaceDispositionRule AppliedDispositionRule);
}
