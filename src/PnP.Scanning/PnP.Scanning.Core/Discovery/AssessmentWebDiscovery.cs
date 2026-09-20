using PnP.Core;
using PnP.Scanning.Core.Storage;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PnP.Scanning.Core.Discovery;

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

    internal async Task RunAsync(IAspxDiscoveryProvider provider, CancellationToken cancellationToken)
    {
        if (provider.RootScope.Kind != DiscoveryScopeKind.Web)
            throw new ArgumentException("The Classic Web worker requires a Web-rooted discovery provider.", nameof(provider));

        try
        {
            var pending = new Queue<DiscoveryScopeRegistration>();
            var scheduled = new HashSet<string>(StringComparer.Ordinal);
            pending.Enqueue(provider.RootScope);
            scheduled.Add(provider.RootScope.ScopeKey);
            await writer.WriteAsync(new[] { Scope(provider.RootScope) }, cancellationToken).ConfigureAwait(false);
            while (pending.TryDequeue(out var scope))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!scope.Required) continue;
                var resultRow = Scope(scope);
                var rawOutcome = DiscoveryTerminalOutcome.Complete;
                if (scope.SourceKind != null)
                {
                    rawOutcome = await ReadSurfaceAsync(provider, scope, resultRow, cancellationToken).ConfigureAwait(false);
                }

                DiscoveryChildEnumerationResult children;
                try { children = await provider.EnumerateChildrenAsync(scope, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    AddError(resultRow, "EnumerateChildren", ErrorCode(ex), ErrorDetail(ex));
                    children = new(AspxDiscoveryHierarchy.ChildKindFor(scope.Kind), Array.Empty<DiscoveryChildExpectation>(),
                        Array.Empty<DiscoveryScopeRegistration>(), Classify(ex), scope.PermissionContext);
                }

                var childKeys = children.ObservedChildren.Select(child => child.ScopeKey).ToHashSet(StringComparer.Ordinal);
                resultRow.ObservedChildCount = childKeys.Count;
                resultRow.ExpectedChildCount = Success(children.Outcome) ? children.ExpectedChildren.Count : null;
                var outcome = !Success(rawOutcome) ? rawOutcome : children.Outcome;
                resultRow.DiscoveryStatus = Status(outcome);
                AddError(resultRow, "EnumerateChildren", children.GapCode, children.GapDetail);
                if (!Success(outcome) && string.IsNullOrWhiteSpace(resultRow.ErrorCodes))
                    AddError(resultRow, "Discovery", "scope_" + outcome.ToString().ToLowerInvariant(),
                        "This scope did not finish successfully; its unobserved contents are unknown.");
                var rows = new List<ClassicPageDiscovery> { resultRow };
                foreach (var expected in children.ExpectedChildren.Where(child => child.Required && !childKeys.Contains(child.ScopeKey)))
                {
                    var missing = Scope(new(expected.ScopeKey, scope.ScopeKey, expected.Kind, expected.SourceKind,
                        expected.Locator, expected.PermissionContext));
                    missing.DiscoveryStatus = "Unknown";
                    AddError(missing, "EnumerateChildren", DiscoveryGapCodes.ExpectedChildMissing,
                        "The parent declared this child, but it was not returned by enumeration.");
                    rows.Add(missing);
                }
                foreach (var child in children.ObservedChildren)
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
                var collector = referenceProvider.ReferenceCollector;
                var surfaces = collector.ReadSurfaceEvidence()
                    .GroupBy(row => row.SurfaceId, StringComparer.Ordinal).Select(group =>
                {
                    // Preserve a failing observation even if a later surface visit succeeds.
                    var surface = group.FirstOrDefault(row => !Success(row.TerminalOutcome)) ?? group.Last();
                    var row = SurfaceEvidence(surface);
                    if (!Success(surface.TerminalOutcome))
                        AddError(row, surface.RequiredAdapter,
                            "surface_" + surface.TerminalOutcome.ToString().ToLowerInvariant(),
                            "An API surface did not complete; inspect the endpoint and discovery status.");
                    return row;
                });
                var references = collector.ReadReferenceEvidence().Select(ReferenceEvidence);
                var pagination = collector.ReadPaginationEvidence().Select(PaginationEvidence);
                var gaps = collector.ReadGaps().Select(GapEvidence);
                await writer.WriteAsync(surfaces.Concat(references).Concat(pagination).Concat(gaps),
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task<DiscoveryTerminalOutcome> ReadSurfaceAsync(IAspxDiscoveryProvider provider,
        DiscoveryScopeRegistration scope, ClassicPageDiscovery scopeRow, CancellationToken cancellationToken)
    {
        IAsyncEnumerator<RawDiscoveryBatch> enumerator;
        try
        {
            var source = provider.CreateRawSource(scope);
            if (source == null)
            {
                AddError(scopeRow, "ReadFiles", DiscoveryGapCodes.SourceUnsupported, "No source is available for this scope.");
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
            var checkpoints = new HashSet<string>(StringComparer.Ordinal);
            while (true)
            {
                bool available;
                try { available = await enumerator.MoveNextAsync().ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    AddError(scopeRow, "ReadFiles", ErrorCode(ex), ErrorDetail(ex));
                    return Classify(ex);
                }
                if (!available) break;
                var batch = enumerator.Current;
                var pages = new List<ClassicPageDiscovery>();
                foreach (var record in batch.Records ?? Array.Empty<RawDiscoveryRecord>())
                {
                    var admission = AspxAdmission.Evaluate(record);
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
        AddError(scopeRow, "ReadFiles", DiscoveryGapCodes.PaginationTokenLoopOrLoss,
            "The source ended without a terminal batch or repeated a pagination checkpoint.");
        return DiscoveryTerminalOutcome.Truncated;
    }

    private ClassicPageDiscovery Scope(DiscoveryScopeRegistration scope) => new()
    {
        ScanId = scanId, SiteUrl = siteUrl, WebUrl = webUrl, RecordKey = "scope:" + scope.ScopeKey,
        RowType = "Scope", ScopeType = scope.Kind == DiscoveryScopeKind.Container
            ? (scope.Metadata?.GetValueOrDefault("role") == "web-root" ? "WebRoot" : "List") : scope.Kind.ToString(),
        ParentScopeKey = scope.ParentScopeKey == null ? null : "scope:" + scope.ParentScopeKey,
        Url = scope.Locator, DiscoveryStatus = scope.Required ? "Pending" : "PolicyExcluded",
        ObservationMethod = scope.SourceKind?.ToString(), ObservedAtUtc = DateTime.UtcNow,
        SiteCollectionId = MetadataGuid(scope, "siteCollectionId"), WebId = MetadataGuid(scope, "webId"),
        ListId = MetadataGuid(scope, "listId"), FolderUniqueId = MetadataGuid(scope, "folderUniqueId"),
    };

    internal static ClassicPageDiscovery Page(Guid scanId, string siteUrl, string webUrl,
        DiscoveryScopeRegistration scope, RawDiscoveryRecord record)
    {
        var fileId = Guid.TryParse(record.FileUniqueId, out var parsed) && parsed != Guid.Empty ? parsed : (Guid?)null;
        var siteIdentity = record.SiteCollectionId?.ToString("D") ?? siteUrl.ToLowerInvariant();
        var webIdentity = record.WebId?.ToString("D") ?? webUrl.ToLowerInvariant();
        return new()
        {
            ScanId = scanId, SiteUrl = siteUrl, WebUrl = webUrl,
            RecordKey = "page:" + DiscoveryHash.Of(siteIdentity, webIdentity,
                fileId?.ToString("D") ?? record.PhysicalLocator?.ToLowerInvariant() ?? record.SourceObjectId),
            RowType = "Page", ScopeType = "File", ParentScopeKey = "scope:" + scope.ScopeKey,
            Url = record.PhysicalLocator, FileName = record.FileName, FileUniqueId = fileId,
            SiteCollectionId = record.SiteCollectionId, WebId = record.WebId, ListId = record.ListId,
            FolderUniqueId = record.FolderUniqueId, ListItemId = record.ListItemId,
            HomePage = record.HomePage, PageType = record.PageType, ContentTypeId = record.ContentTypeId,
            LibraryHidden = record.LibraryHidden, ObservationMethod = record.ObservationMethod ?? scope.SourceKind?.ToString(),
            DiscoveryStatus = "Discovered", ObservedAtUtc = DateTime.UtcNow,
        };
    }

    internal static void AddError(ClassicPageDiscovery row, string stage, string code, string detail)
    {
        if (string.IsNullOrWhiteSpace(code) && string.IsNullOrWhiteSpace(detail)) return;
        row.ErrorStage = AssessmentDiscoveryWriter.Join(row.ErrorStage, stage);
        row.ErrorCodes = AssessmentDiscoveryWriter.Join(row.ErrorCodes, code);
        row.ErrorDetail = AssessmentDiscoveryWriter.Join(row.ErrorDetail, detail, "\n");
    }

    private ClassicPageDiscovery SurfaceEvidence(AspxSurfaceDenominatorRow surface) => new()
    {
        ScanId = scanId,
        SiteUrl = siteUrl,
        WebUrl = webUrl,
        RecordKey = "surface:" + DiscoveryHash.Of(siteUrl, webUrl, surface.SurfaceId),
        ParentScopeKey = "scope:" + surface.ScopeKey,
        RowType = "Scope",
        ScopeType = "Surface",
        Url = surface.ActualEndpoint,
        ObservationMethod = surface.RequiredAdapter,
        DiscoveryStatus = Status(surface.TerminalOutcome),
        ExpectedChildCount = surface.ExpectedCount,
        ObservedChildCount = surface.ObservedCount,
        EvidenceJson = EvidenceJson(surface),
        ObservedAtUtc = surface.AsOfUtc.UtcDateTime,
    };

    private ClassicPageDiscovery ReferenceEvidence(AspxReferenceCandidate reference)
    {
        var status = reference.Disposition switch
        {
            AspxReferenceDispositions.LinkedPhysicalGhosted or
                AspxReferenceDispositions.LinkedPhysicalCustomized => "Discovered",
            AspxReferenceDispositions.ReferenceUnavailable => "Failed",
            AspxReferenceDispositions.Unknown => "Unknown",
            _ => "Complete",
        };
        return new()
        {
            ScanId = scanId,
            SiteUrl = siteUrl,
            WebUrl = webUrl,
            RecordKey = "reference:" + DiscoveryHash.Of(siteUrl, webUrl, reference.SourceKind,
                reference.SourceObjectId, reference.RawLocator),
            RowType = "Reference",
            ScopeType = reference.SourceKind,
            Url = reference.RawLocator,
            FileUniqueId = Guid.TryParse(reference.LinkedFileUniqueId, out var fileId) ? fileId : null,
            ObservationMethod = reference.AcquisitionMethod,
            DiscoveryStatus = status,
            ErrorCodes = status is "Failed" or "Unknown" ? reference.ReasonCode : null,
            EvidenceJson = EvidenceJson(reference),
            ObservedAtUtc = DateTime.UtcNow,
        };
    }

    private ClassicPageDiscovery PaginationEvidence(AspxPaginationPageReceipt page)
    {
        var denied = page.HttpStatusCode is 401 or 403 ||
            page.SemanticDetectorResult is SharePointSemanticDetectorResults.AccessDenied or
                SharePointSemanticDetectorResults.Unauthorized or SharePointSemanticDetectorResults.LoginShell;
        var status = denied ? "Denied" : !string.IsNullOrWhiteSpace(page.ErrorCode) ? "Failed" :
            page.TerminalFlag ? "Complete" : "Pending";
        return new()
        {
            ScanId = scanId,
            SiteUrl = siteUrl,
            WebUrl = webUrl,
            RecordKey = "pagination:" + DiscoveryHash.Of(siteUrl, webUrl, page.CollectionScopeKey,
                page.PageOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture),
                page.ActualEndpointHash, page.RequestTokenHash),
            ParentScopeKey = "scope:" + page.CollectionScopeKey,
            RowType = "Pagination",
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

    private ClassicPageDiscovery GapEvidence(string gap) => new()
    {
        ScanId = scanId,
        SiteUrl = siteUrl,
        WebUrl = webUrl,
        RecordKey = "gap:" + DiscoveryHash.Of(siteUrl, webUrl, gap),
        RowType = "Gap",
        ScopeType = "Evidence",
        DiscoveryStatus = "Unknown",
        ErrorStage = "AcquisitionEvidence",
        ErrorCodes = gap,
        ObservedAtUtc = DateTime.UtcNow,
    };

    private static string EvidenceJson<T>(T value) => JsonSerializer.Serialize(value, EvidenceJsonOptions);

    private static readonly JsonSerializerOptions EvidenceJsonOptions = CreateEvidenceJsonOptions();

    private static JsonSerializerOptions CreateEvidenceJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    internal static bool Success(DiscoveryTerminalOutcome outcome) => outcome is DiscoveryTerminalOutcome.Complete or DiscoveryTerminalOutcome.Empty;
    internal static string Status(DiscoveryTerminalOutcome outcome) => outcome == DiscoveryTerminalOutcome.Truncated ? "Partial" : outcome.ToString();
    internal static DiscoveryTerminalOutcome Classify(Exception ex) => ex is ServiceException { Error: ServiceError { HttpResponseCode: 401 or 403 } }
        || ex is UnauthorizedAccessException ? DiscoveryTerminalOutcome.Denied : DiscoveryTerminalOutcome.Failed;
    internal static string ErrorCode(Exception ex) => ex is ServiceException { Error: ServiceError error }
        ? $"HTTP{error.HttpResponseCode}:{error.Code}" : ex.GetType().Name;
    internal static string ErrorDetail(Exception ex) => ex is ServiceException { Error: ServiceError error }
        ? $"{error.Message}; requestId={error.ClientRequestId}" : ex.Message;
    private static Guid? MetadataGuid(DiscoveryScopeRegistration scope, string name) => scope.Metadata != null &&
        Guid.TryParse(scope.Metadata.GetValueOrDefault(name), out var value) && value != Guid.Empty ? value : null;
}
