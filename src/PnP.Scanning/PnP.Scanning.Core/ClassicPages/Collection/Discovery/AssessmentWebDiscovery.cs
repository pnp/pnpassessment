using PnP.Scanning.Core.ClassicPages.Compatibility;
using PnP.Core;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.ClassicPages.Contracts.Page;
using PnP.Scanning.Core.ClassicPages.Analysis.Page;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PnP.Scanning.Core.ClassicPages.Collection.Discovery;

/// <summary>
/// Web-local acquisition executed by the existing Classic WebQueue. The provider and its PnP contexts
/// belong to one worker; batches are committed before the next scope is requested.
/// </summary>
internal sealed class AssessmentWebDiscovery
{
    private readonly Guid scanId;
    private readonly string siteUrl;
    private readonly string webUrl;
    private readonly AssessmentDiscoveryWriter writer;

    internal AssessmentWebDiscovery(Guid scanId, string siteUrl, string webUrl, AssessmentDiscoveryWriter writer)
    {
        this.scanId = scanId;
        this.siteUrl = siteUrl;
        this.webUrl = webUrl;
        this.writer = writer;
    }

    /// <summary>Traverses required discovery scopes, captures each batch and retains reference receipts even on failure.</summary>
    internal async Task RunAsync(IAspxDiscoveryProvider provider, CancellationToken cancellationToken)
    {
        if (provider.RootScope.Kind != DiscoveryScopeKind.Web)
            throw new ArgumentException(
                "The Classic Web worker requires a Web-rooted discovery provider.",
                nameof(provider));

        try
        {
            Queue<DiscoveryScopeRegistration> pending = new Queue<DiscoveryScopeRegistration>();
            HashSet<string> scheduled = new HashSet<string>(StringComparer.Ordinal);
            pending.Enqueue(provider.RootScope);
            scheduled.Add(provider.RootScope.ScopeKey);
            await writer.WriteAsync(new[] { Scope(provider.RootScope) }, cancellationToken).ConfigureAwait(false);
            while (pending.TryDequeue(out DiscoveryScopeRegistration scope))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!scope.Required) continue;
                DiscoveryEvidence resultRow = Scope(scope);
                DiscoveryTerminalOutcome rawOutcome = DiscoveryTerminalOutcome.Complete;
                if (scope.SourceKind != null)
                {
                    rawOutcome = await ReadSurfaceAsync(provider, scope, resultRow, cancellationToken)
                        .ConfigureAwait(false);
                }

                DiscoveryChildEnumerationResult children;
                try
                {
                    children = await provider.EnumerateChildrenAsync(scope, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AddError(resultRow, "EnumerateChildren", ErrorCode(ex), ErrorDetail(ex));
                    children = new(
                        AspxDiscoveryHierarchy.ChildKindFor(scope.Kind),
                        Array.Empty<DiscoveryChildExpectation>(),
                        Array.Empty<DiscoveryScopeRegistration>(),
                        Classify(ex),
                        scope.PermissionContext);
                }

                HashSet<string> childKeys = children.ObservedChildren.Select(child => child.ScopeKey)
                    .ToHashSet(StringComparer.Ordinal);
                resultRow.ObservedChildCount = childKeys.Count;
                resultRow.ExpectedChildCount = Success(children.Outcome) ? children.ExpectedChildren.Count : null;
                DiscoveryTerminalOutcome outcome = !Success(rawOutcome) ? rawOutcome : children.Outcome;
                resultRow.DiscoveryStatus = Status(outcome);
                AddError(resultRow, "EnumerateChildren", children.GapCode, children.GapDetail);
                if (!Success(outcome) && string.IsNullOrWhiteSpace(resultRow.ErrorCodes))
                    AddError(
                        resultRow,
                        "Discovery",
                        "scope_" + outcome.ToString().ToLowerInvariant(),
                        "This scope did not finish successfully; its unobserved contents are unknown.");
                List<DiscoveryEvidence> rows = new List<DiscoveryEvidence> { resultRow };
                foreach (DiscoveryChildExpectation expected in children.ExpectedChildren.Where(child =>
                             child.Required && !childKeys.Contains(child.ScopeKey)))
                {
                    DiscoveryEvidence missing = Scope(
                        new(
                            expected.ScopeKey,
                            scope.ScopeKey,
                            expected.Kind,
                            expected.SourceKind,
                            expected.Locator,
                            expected.PermissionContext));
                    missing.DiscoveryStatus = "Unknown";
                    AddError(
                        missing,
                        "EnumerateChildren",
                        DiscoveryGapCodes.ExpectedChildMissing,
                        "The parent declared this child, but it was not returned by enumeration.");
                    rows.Add(missing);
                }

                foreach (DiscoveryScopeRegistration child in children.ObservedChildren)
                {
                    if (!scheduled.Add(child.ScopeKey)) continue;
                    rows.Add(Scope(child));
                    pending.Enqueue(child);
                }

                // Failed/unknown parents retain already discovered children. Missing children remain
                // Scope rows, never fabricated Page rows. Register children before attempting their reads.
                await writer.WriteAsync(rows, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (provider is IAspxReferenceAcquisitionProvider referenceProvider)
            {
                AspxReferenceCollector collector = referenceProvider.ReferenceCollector;
                IEnumerable<DiscoveryEvidence> surfaces = collector.ReadSurfaceEvidence()
                    .GroupBy(row => row.SurfaceId, StringComparer.Ordinal)
                    .Select(group =>
                    {
                        // Preserve a failing observation even if a later surface visit succeeds.
                        AspxSurfaceDenominatorRow surface =
                            group.FirstOrDefault(row => !Success(row.TerminalOutcome)) ?? group.Last();
                        DiscoveryEvidence row = SurfaceEvidence(surface);
                        if (!Success(surface.TerminalOutcome))
                            AddError(
                                row,
                                surface.RequiredAdapter,
                                "surface_" + surface.TerminalOutcome.ToString().ToLowerInvariant(),
                                "An API surface did not complete; inspect the endpoint and discovery status.");
                        return row;
                    });
                IEnumerable<DiscoveryEvidence> references = collector.ReadReferenceEvidence().Select(ReferenceEvidence);
                IEnumerable<DiscoveryEvidence> pagination =
                    collector.ReadPaginationEvidence().Select(PaginationEvidence);
                IEnumerable<DiscoveryEvidence> gaps = collector.ReadGaps().Select(GapEvidence);
                await writer.WriteAsync(
                        surfaces.Concat(references).Concat(pagination).Concat(gaps),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>Commits raw physical facts before ASPX admission and records surface outcomes without converting persistence failures into API gaps.</summary>
    private async Task<DiscoveryTerminalOutcome> ReadSurfaceAsync(
        IAspxDiscoveryProvider provider,
        DiscoveryScopeRegistration scope,
        DiscoveryEvidence scopeRow,
        CancellationToken cancellationToken)
    {
        IAsyncEnumerator<RawDiscoveryBatch> enumerator;
        try
        {
            IRawDiscoverySource source = provider.CreateRawSource(scope);
            if (source == null)
            {
                AddError(
                    scopeRow,
                    "ReadFiles",
                    DiscoveryGapCodes.SourceUnsupported,
                    "No source is available for this scope.");
                return DiscoveryTerminalOutcome.Unknown;
            }

            enumerator = source.ReadBatchesAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AddError(scopeRow, "ReadFiles", ErrorCode(ex), ErrorDetail(ex));
            return Classify(ex);
        }

        await using (enumerator)
        {
            HashSet<string> checkpoints = new HashSet<string>(StringComparer.Ordinal);
            while (true)
            {
                bool available;
                try
                {
                    available = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AddError(scopeRow, "ReadFiles", ErrorCode(ex), ErrorDetail(ex));
                    return Classify(ex);
                }

                if (!available) break;
                RawDiscoveryBatch batch = enumerator.Current;
                await writer.WritePhysicalFilesAsync(
                        scanId,
                        batch.Records ?? Array.Empty<RawDiscoveryRecord>(),
                        cancellationToken)
                    .ConfigureAwait(false);
                List<DiscoveryEvidence> pages = new List<DiscoveryEvidence>();
                foreach (RawDiscoveryRecord record in batch.Records ?? Array.Empty<RawDiscoveryRecord>())
                {
                    AspxAdmissionResult admission = AspxAdmission.Evaluate(record);
                    if (!admission.IsAspx)
                    {
                        AddError(scopeRow, "ReadFiles", admission.GapCode, admission.Detail);
                        continue;
                    }

                    pages.Add(Page(scanId, siteUrl, webUrl, scope, record));
                }

                // Storage failures are deliberately outside the request exception handlers.
                // A failed commit must fail the worker rather than be misreported as an API gap.
                await writer.WriteAsync(pages, cancellationToken).ConfigureAwait(false);
                AddError(scopeRow, "ReadFiles", batch.GapCode, batch.GapDetail);
                if (!string.IsNullOrWhiteSpace(batch.NextCheckpoint) && !checkpoints.Add(batch.NextCheckpoint)) break;
                if (batch.IsTerminal)
                {
                    if (!string.IsNullOrWhiteSpace(batch.NextCheckpoint)) break;
                    return batch.TerminalOutcome;
                }
            }
        }

        AddError(
            scopeRow,
            "ReadFiles",
            DiscoveryGapCodes.PaginationTokenLoopOrLoss,
            "The source ended without a terminal batch or repeated a pagination checkpoint.");
        return DiscoveryTerminalOutcome.Truncated;
    }

    /// <summary>Projects a registered discovery scope without inventing file or child identities.</summary>
    private DiscoveryEvidence Scope(DiscoveryScopeRegistration scope) =>
        new()
        {
            ScanId = scanId,
            SiteUrl = siteUrl,
            WebUrl = webUrl,
            RecordKey = "scope:" + scope.ScopeKey,
            RowType = DiscoveryRowType.Scope,
            ScopeType =
                scope.Kind == DiscoveryScopeKind.Container
                    ? (scope.Metadata?.GetValueOrDefault("role") == "web-root" ? "WebRoot" : "List")
                    : scope.Kind.ToString(),
            ParentScopeKey = scope.ParentScopeKey == null ? null : "scope:" + scope.ParentScopeKey,
            Url = scope.Locator,
            DiscoveryStatus = scope.Required ? "Pending" : "PolicyExcluded",
            ObservationMethod = scope.SourceKind?.ToString(),
            ObservedAtUtc = DateTime.UtcNow,
            SiteCollectionId = MetadataGuid(scope, "siteCollectionId"),
            WebId = MetadataGuid(scope, "webId"),
            ListId = MetadataGuid(scope, "listId"),
            FolderUniqueId = MetadataGuid(scope, "folderUniqueId"),
        };

    /// <summary>Projects discovered native identity and customization evidence without resolving an item into a file.</summary>
    internal static DiscoveryEvidence Page(
        Guid scanId,
        string siteUrl,
        string webUrl,
        DiscoveryScopeRegistration scope,
        RawDiscoveryRecord record)
    {
        Guid? fileId = Guid.TryParse(record.FileUniqueId, out Guid parsed) && parsed != Guid.Empty
            ? parsed
            : (Guid?)null;
        string siteIdentity = record.SiteCollectionId?.ToString("D") ?? siteUrl.ToLowerInvariant();
        string webIdentity = record.WebId?.ToString("D") ?? webUrl.ToLowerInvariant();
        DiscoveryEvidence row = new DiscoveryEvidence
        {
            ScanId = scanId,
            SiteUrl = siteUrl,
            WebUrl = webUrl,
            RecordKey = "page:" + DiscoveryHash.Of(
                siteIdentity,
                webIdentity,
                fileId?.ToString("D") ?? record.PhysicalLocator?.ToLowerInvariant() ?? record.SourceObjectId),
            // "File" asserts the row represents a file. A page addressed through a shared display form
            // does not: it is one of N addresses backed by one form file, so it is a ListItem.
            RowType = DiscoveryRowType.Page,
            ScopeType = record.LocatorIsGuaranteedPhysicalFilePath ? "File" : "ListItem",
            ParentScopeKey = "scope:" + scope.ScopeKey,
            Url = record.PhysicalLocator,
            FileName = record.FileName,
            FileUniqueId = fileId,
            SiteCollectionId = record.SiteCollectionId,
            WebId = record.WebId,
            ListId = record.ListId,
            FolderUniqueId = record.FolderUniqueId,
            ListItemId = record.ListItemId,
            HomePage = record.HomePage,
            PageType = record.PageType,
            ContentTypeId = record.ContentTypeId,
            SetupPath = record.SetupPath,
            LibraryHidden = record.LibraryHidden,
            ObservationMethod = record.ObservationMethod ?? scope.SourceKind?.ToString(),
            DiscoveryStatus = "Discovered",
            ObservedAtUtc = DateTime.UtcNow,
        };
        try
        {
            row.CustomizedPageStatus = FileCustomizationStatus.Normalize(record.CustomizedPageStatusRaw);
        }
        catch (InvalidDataException ex)
        {
            AddError(row, "PageCustomizationMetadata", "InvalidCustomizedPageStatus", ex.Message);
        }

        return row;
    }

    /// <summary>Appends distinct acquisition stages, codes and complete error messages.</summary>
    internal static void AddError(DiscoveryEvidence row, string stage, string code, string detail)
    {
        if (string.IsNullOrWhiteSpace(code) && string.IsNullOrWhiteSpace(detail)) return;
        row.ErrorStage = DiscoveryEvidenceRules.Join(row.ErrorStage, stage);
        row.ErrorCodes = DiscoveryEvidenceRules.Join(row.ErrorCodes, code);
        row.ErrorDetail = DiscoveryEvidenceRules.Join(row.ErrorDetail, detail, "\n");
    }

    /// <summary>Appends acquisition errors to retained legacy rows at the persistence boundary.</summary>
    internal static void AddError(ClassicPageDiscovery row, string stage, string code, string detail)
    {
        if (string.IsNullOrWhiteSpace(code) && string.IsNullOrWhiteSpace(detail)) return;
        row.ErrorStage = DiscoveryEvidenceRules.Join(row.ErrorStage, stage);
        row.ErrorCodes = DiscoveryEvidenceRules.Join(row.ErrorCodes, code);
        row.ErrorDetail = DiscoveryEvidenceRules.Join(row.ErrorDetail, detail, "\n");
    }

    /// <summary>Projects an API authority's counts, endpoint and terminal outcome into native scope evidence.</summary>
    private DiscoveryEvidence SurfaceEvidence(AspxSurfaceDenominatorRow surface) =>
        new()
        {
            ScanId = scanId,
            SiteUrl = siteUrl,
            WebUrl = webUrl,
            RecordKey = "surface:" + DiscoveryHash.Of(siteUrl, webUrl, surface.SurfaceId),
            ParentScopeKey = "scope:" + surface.ScopeKey,
            RowType = DiscoveryRowType.Scope,
            ScopeType = "Surface",
            Url = surface.ActualEndpoint,
            ObservationMethod = surface.RequiredAdapter,
            DiscoveryStatus = Status(surface.TerminalOutcome),
            ExpectedChildCount = surface.ExpectedCount,
            ObservedChildCount = surface.ObservedCount,
            EvidenceJson = EvidenceJson(surface),
            ObservedAtUtc = surface.AsOfUtc.UtcDateTime,
        };

    /// <summary>
    /// Projects a reference into scan- and web-scoped evidence with its declaration scope, optional physical link, and failure diagnostics,
    /// without promoting the reference to a physical page observation.
    /// </summary>
    private DiscoveryEvidence ReferenceEvidence(AspxReferenceCandidate reference)
    {
        string status = reference.Disposition switch
        {
            AspxReferenceDispositions.LinkedPhysicalGhosted
                or AspxReferenceDispositions.LinkedPhysicalCustomized => "Discovered",
            AspxReferenceDispositions.ReferenceUnavailable => "Failed",
            AspxReferenceDispositions.Unknown => "Unknown",
            _ => "Complete",
        };
        return new()
        {
            ScanId = scanId,
            SiteUrl = siteUrl,
            WebUrl = webUrl,
            RecordKey =
                "reference:" + DiscoveryHash.Of(
                    siteUrl,
                    webUrl,
                    reference.SourceKind,
                    reference.SourceObjectId,
                    reference.RawLocator),
            RowType = DiscoveryRowType.Reference,
            ScopeType = reference.SourceKind,
            Url = reference.RawLocator,
            FileUniqueId = Guid.TryParse(reference.LinkedFileUniqueId, out Guid fileId) ? fileId : null,
            ListId = reference.ListId,
            ContentTypeId = reference.ContentTypeId,
            ObservationMethod = reference.AcquisitionMethod,
            DiscoveryStatus = status,
            ErrorCodes = status is "Failed" or "Unknown" ? reference.ReasonCode : null,
            ErrorStage = status is "Failed" or "Unknown" ? reference.ErrorStage : null,
            ErrorDetail = status is "Failed" or "Unknown" ? reference.ErrorDetail : null,
            EvidenceJson = EvidenceJson(reference),
            ObservedAtUtc = DateTime.UtcNow,
        };
    }

    /// <summary>Retains request-level paging identities, continuations and validation as native evidence.</summary>
    private DiscoveryEvidence PaginationEvidence(AspxPaginationPageReceipt page)
    {
        bool denied = page.HttpStatusCode is 401 or 403 ||
                      page.SemanticDetectorResult is SharePointSemanticDetectorResults.AccessDenied
                          or SharePointSemanticDetectorResults.Unauthorized
                          or SharePointSemanticDetectorResults.LoginShell;
        string status = denied
            ? "Denied"
            : !string.IsNullOrWhiteSpace(page.ErrorCode)
                ? "Failed"
                : page.TerminalFlag
                    ? "Complete"
                    : "Pending";
        return new()
        {
            ScanId = scanId,
            SiteUrl = siteUrl,
            WebUrl = webUrl,
            RecordKey =
                "pagination:" + DiscoveryHash.Of(
                    siteUrl,
                    webUrl,
                    page.CollectionScopeKey,
                    page.PageOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    page.ActualEndpointHash,
                    page.RequestTokenHash),
            ParentScopeKey = "scope:" + page.CollectionScopeKey,
            RowType = DiscoveryRowType.Pagination,
            ScopeType = "RequestPage",
            Url = page.ActualEndpoint,
            ObservationMethod = page.ActualMethod,
            DiscoveryStatus = status,
            ObservedChildCount = page.ResponseItemCount,
            ErrorCodes = page.ErrorCode,
            EvidenceJson = EvidenceJson(page),
            ObservedAtUtc = page.ReceivedAtUtc.UtcDateTime,
        };
    }

    /// <summary>Retains an acquisition gap independently of any discovered physical Page.</summary>
    private DiscoveryEvidence GapEvidence(string gap) =>
        new()
        {
            ScanId = scanId,
            SiteUrl = siteUrl,
            WebUrl = webUrl,
            RecordKey = "gap:" + DiscoveryHash.Of(siteUrl, webUrl, gap),
            RowType = DiscoveryRowType.Gap,
            ScopeType = "Evidence",
            DiscoveryStatus = "Unknown",
            ErrorStage = "AcquisitionEvidence",
            ErrorCodes = gap,
            ObservedAtUtc = DateTime.UtcNow,
        };

    /// <summary>Serializes native acquisition evidence with the existing explicit enum-label options.</summary>
    private static string EvidenceJson<T>(T value) => JsonSerializer.Serialize(value, EvidenceJsonOptions);

    private static readonly JsonSerializerOptions EvidenceJsonOptions = CreateEvidenceJsonOptions();

    /// <summary>Creates the string-enum serializer settings for raw discovery evidence.</summary>
    private static JsonSerializerOptions CreateEvidenceJsonOptions()
    {
        JsonSerializerOptions options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    /// <summary>Recognizes complete or empty native discovery outcomes.</summary>
    internal static bool Success(DiscoveryTerminalOutcome outcome) =>
        outcome is DiscoveryTerminalOutcome.Complete or DiscoveryTerminalOutcome.Empty;

    /// <summary>Maps a native terminal outcome to its existing persisted discovery label.</summary>
    internal static string Status(DiscoveryTerminalOutcome outcome) =>
        outcome == DiscoveryTerminalOutcome.Truncated ? "Partial" : outcome.ToString();

    /// <summary>Classifies an exception for retained acquisition failure evidence.</summary>
    internal static DiscoveryTerminalOutcome Classify(Exception ex) =>
        ex is ServiceException { Error: ServiceError { HttpResponseCode: 401 or 403 } } ||
        ex is UnauthorizedAccessException
            ? DiscoveryTerminalOutcome.Denied
            : DiscoveryTerminalOutcome.Failed;

    /// <summary>Retains the native error code or original exception type for acquisition diagnostics.</summary>
    internal static string ErrorCode(Exception ex) =>
        ex is ServiceException { Error: ServiceError error }
            ? $"HTTP{error.HttpResponseCode}:{error.Code}"
            : ex.GetType().Name;

    /// <summary>Retains complete structured acquisition error detail, including native causes.</summary>
    internal static string ErrorDetail(Exception ex) =>
        ex is ServiceException { Error: ServiceError error }
            ? $"{error.Message}; requestId={error.ClientRequestId}"
            : ex.Message;

    /// <summary>Reads a captured optional GUID without generating an identity.</summary>
    private static Guid? MetadataGuid(DiscoveryScopeRegistration scope, string name) =>
        scope.Metadata != null && Guid.TryParse(scope.Metadata.GetValueOrDefault(name), out Guid value) &&
        value != Guid.Empty
            ? value
            : null;
}