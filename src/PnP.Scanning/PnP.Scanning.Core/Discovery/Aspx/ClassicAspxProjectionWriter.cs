using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Storage;
using System.Text.Json;

namespace PnP.Scanning.Core.Discovery.Aspx;

/// <summary>Bounded, atomic materialization of observed identities and distinct scoped Item pairs.</summary>
internal sealed class ClassicAspxProjectionWriter
{
    internal const int MaximumBatchSize = 256;
    private const string ChangedItemIdentityReadState = "ChangedDuringScan";
    private const string EmptyProvenance = "[]";

    private static class HandlerParseStates
    {
        internal const string NotParsed = "NotParsed";
        internal const string Declared = "Declared";
        internal const string NoInherits = "NoInherits";
    }
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly Func<ScanContext> createContext;

    /// <summary>Creates a writer whose fresh database contexts target the specified assessment scan.</summary>
    internal ClassicAspxProjectionWriter(Guid scanId) : this(() => new ScanContext(scanId))
    {
    }

    /// <summary>Retains a context factory so each serialized atomic batch owns and disposes its own context.</summary>
    internal ClassicAspxProjectionWriter(Func<ScanContext> createContext) =>
        this.createContext = createContext ?? throw new ArgumentNullException(nameof(createContext));

    /// <summary>Validates a bounded single-scan batch, merges identities and distinct item pairs, and commits both projections atomically.</summary>
    internal async Task WriteAsync(IEnumerable<ClassicAspxInventory> inventories,
        IEnumerable<ClassicAspxItemBinding> bindings, CancellationToken cancellationToken = default)
    {
        var files = Bounded(inventories);
        var pairs = Bounded(bindings);
        if (files.Length == 0 && pairs.Length == 0)
        {
            return;
        }
        foreach (var file in files)
        {
            Validate(file);
        }
        foreach (var pair in pairs)
        {
            Validate(pair);
        }
        var scan = files.FirstOrDefault()?.ScanId ?? pairs[0].ScanId;
        if (files.Any(row => row.ScanId != scan) || pairs.Any(row => row.ScanId != scan))
            throw new ArgumentException("A projection batch belongs to exactly one scan.");
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var db = createContext();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            foreach (var observation in files)
            {
                var previous = await db.ClassicAspxInventories.FindAsync(
                    new object[] { observation.ScanId, observation.AspxKey }, cancellationToken).ConfigureAwait(false);
                if (previous == null)
                {
                    observation.ProvenanceJson = UnionProvenance(EmptyProvenance, observation.ProvenanceJson);
                    if (observation.FirstObservedAtUtc == default)
                    {
                        observation.FirstObservedAtUtc = observation.ObservedAtUtc;
                    }
                    db.ClassicAspxInventories.Add(observation);
                }
                else
                {
                    Merge(previous, observation);
                }
            }
            foreach (var observation in pairs)
            {
                var previous = await db.ClassicAspxItemBindings.FindAsync(new object[]
                {
                    observation.ScanId, observation.AspxKey, observation.SiteCollectionId,
                    observation.WebId, observation.ListId, observation.ItemId
                }, cancellationToken).ConfigureAwait(false);
                if (previous == null)
                {
                    observation.BindingProvenanceJson = UnionProvenance(EmptyProvenance, observation.BindingProvenanceJson);
                    db.ClassicAspxItemBindings.Add(observation);
                }
                else
                {
                    previous.BindingKinds |= observation.BindingKinds;
                    previous.BindingProvenanceJson = UnionProvenance(previous.BindingProvenanceJson, observation.BindingProvenanceJson);
                    if (previous.ItemGuid.HasValue && observation.ItemGuid.HasValue && previous.ItemGuid != observation.ItemGuid)
                        previous.ItemGuidReadState = ChangedItemIdentityReadState;
                    else if (observation.ItemGuid.HasValue)
                    {
                        previous.ItemGuid = observation.ItemGuid;
                        previous.ItemGuidReadState = observation.ItemGuidReadState;
                    }
                    previous.SiteUrl = observation.SiteUrl ?? previous.SiteUrl;
                    previous.WebUrl = observation.WebUrl ?? previous.WebUrl;
                    previous.ItemUrl = observation.ItemUrl ?? previous.ItemUrl;
                    previous.ItemTitle = observation.ItemTitle ?? previous.ItemTitle;
                    previous.ContentTypeId = observation.ContentTypeId ?? previous.ContentTypeId;
                    previous.ObservedAtUtc = Latest(previous.ObservedAtUtc, observation.ObservedAtUtc);
                }
            }
            // FK failures and storage failures are not API gaps. Neither the batch nor a partial count is committed.
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Seeds bounded versioned catalog identities without asserting tenant item coverage or acquiring file bytes.</summary>
    internal async Task SeedCatalogAsync(Guid scanId, PlatformAspxCatalog catalog,
        CancellationToken cancellationToken = default)
    {
        if (catalog == null)
        {
            return;
        }
        var now = DateTime.UtcNow;
        foreach (var chunk in catalog.Entries.Chunk(MaximumBatchSize))
        {
            await WriteAsync(chunk.Select(entry => new ClassicAspxInventory
            {
                ScanId = scanId,
                AspxKey = ClassicAspxInventory.CreatePlatformKey(catalog.Source, catalog.Version, catalog.Digest, entry.AspxKey),
                IdentityKind = ClassicAspxIdentityKinds.PlatformCatalog,
                IdentityVersion = 1,
                Url = entry.Path,
                FileName = entry.Path.Split('/').Last(),
                CatalogSource = catalog.Source,
                CatalogVersion = catalog.Version,
                CatalogDigest = catalog.Digest,
                CatalogEntryKey = entry.AspxKey,
                FileReadState = ClassicAspxReadStates.NotRequested,
                HandlerReadState = ClassicAspxReadStates.NotRequested,
                HandlerParseState = entry.DeclaredInherits == null ? HandlerParseStates.NotParsed : HandlerParseStates.Declared,
                HandlerDeclaredType = entry.DeclaredInherits,
                HandlerNormalizedType = entry.DeclaredInherits?.Trim(),
                HandlerEffectiveType = entry.DeclaredInherits?.Trim(),
                HandlerSource = ClassicAspxHandlerSources.VersionedCatalogReference,
                HandlerReasonCode = entry.HandlerEvidenceSource,
                ProvenanceJson = JsonSerializer.Serialize(new[] { new { source = catalog.Source, version = catalog.Version, digest = catalog.Digest, entry = entry.Id, hive = entry.Hive, paths = entry.Paths } }),
                // Catalog membership proves identity, not that every Item in the scan scope has been observed.
                CoverageState = ClassicAspxCoverageStates.Unknown,
                FirstObservedAtUtc = now,
                ObservedAtUtc = now
            }), Array.Empty<ClassicAspxItemBinding>(), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Materializes at most one batch plus a sentinel and rejects oversized input before any database transaction begins.</summary>
    private static T[] Bounded<T>(IEnumerable<T> rows)
    {
        var result = (rows ?? Array.Empty<T>()).Take(MaximumBatchSize + 1).ToArray();
        if (result.Length > MaximumBatchSize)
        {
            throw new ArgumentException("Projection batches must remain bounded.");
        }
        return result;
    }

    /// <summary>Rejects unsupported identities or handler facts that lack actual-source evidence or an explicit versioned catalog reference.</summary>
    private static void Validate(ClassicAspxInventory row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.ScanId == Guid.Empty || row.IdentityVersion != 1)
        {
            throw new ArgumentException("An observed scan and supported identity version are required.");
        }
        var key = row.IdentityKind switch
        {
            ClassicAspxIdentityKinds.TenantFile => ClassicAspxInventory.CreateTenantFileKey(row.SiteCollectionId ?? Guid.Empty, row.FileUniqueId ?? Guid.Empty),
            ClassicAspxIdentityKinds.PlatformCatalog => ClassicAspxInventory.CreatePlatformKey(row.CatalogSource, row.CatalogVersion, row.CatalogDigest, row.CatalogEntryKey),
            _ => throw new ArgumentException("Unresolved paths cannot become normalized ASPX identities.")
        };
        if (!string.Equals(row.AspxKey, key, StringComparison.Ordinal))
        {
            throw new ArgumentException("ASPX key differs from observed identity.");
        }
        if (row.HandlerEffectiveType != null && row.HandlerSource != ClassicAspxHandlerSources.VersionedCatalogReference &&
            (row.HandlerReadState != ClassicAspxReadStates.Available || row.HandlerSource != ClassicAspxHandlerSources.ActualFileBytes || row.HandlerParseState is not (HandlerParseStates.Declared or HandlerParseStates.NoInherits)))
            throw new ArgumentException("Effective Handler facts need complete actual-source evidence or an explicit versioned catalog reference.");
        if (row.HandlerParseState == HandlerParseStates.NoInherits && row.HandlerEffectiveType != null && string.IsNullOrWhiteSpace(row.HandlerDefaultProvenance))
            throw new ArgumentException("A framework default must retain its assumption provenance.");
        _ = UnionProvenance(EmptyProvenance, row.ProvenanceJson);
    }

    /// <summary>Requires a real scoped item identity, supported binding reasons, and valid array provenance before persistence.</summary>
    private static void Validate(ClassicAspxItemBinding row)
    {
        ArgumentNullException.ThrowIfNull(row);
        const ClassicAspxBindingKinds supported = ClassicAspxBindingKinds.FileSelf | ClassicAspxBindingKinds.ListFormRenderer |
            ClassicAspxBindingKinds.ContentTypeRenderer | ClassicAspxBindingKinds.DocumentMetadataRenderer | ClassicAspxBindingKinds.PublishingPageLayout;
        if (row.ScanId == Guid.Empty || row.SiteCollectionId == Guid.Empty || row.WebId == Guid.Empty || row.ListId == Guid.Empty ||
            row.ItemId <= 0 || string.IsNullOrWhiteSpace(row.AspxKey) || row.ItemGuid == Guid.Empty ||
            row.BindingKinds == ClassicAspxBindingKinds.None || (row.BindingKinds & ~supported) != 0)
            throw new ArgumentException("Bindings require a real scoped positive Item identity and a supported acquisition reason.");
        _ = UnionProvenance(EmptyProvenance, row.BindingProvenanceJson);
    }

    /// <summary>Combines evidence without erasing acquired handler facts and records location or handler changes as scan coverage gaps.</summary>
    private static void Merge(ClassicAspxInventory previous, ClassicAspxInventory incoming)
    {
        previous.ProvenanceJson = UnionProvenance(previous.ProvenanceJson, incoming.ProvenanceJson);
        previous.CoverageState = MergeCoverage(previous.CoverageState, incoming.CoverageState);
        if (!string.IsNullOrWhiteSpace(previous.Url) && !string.IsNullOrWhiteSpace(incoming.Url) &&
            !CanonicalLocator(previous.Url).Equals(CanonicalLocator(incoming.Url), StringComparison.OrdinalIgnoreCase))
        {
            previous.CoverageState = ClassicAspxCoverageStates.ChangedDuringScan;
            previous.ProvenanceJson = UnionProvenance(previous.ProvenanceJson,
                JsonSerializer.Serialize(new[] { new { observation = "location_changed", before = previous.Url, after = incoming.Url } }));
        }
        previous.SiteUrl = incoming.SiteUrl ?? previous.SiteUrl;
        previous.WebUrl = incoming.WebUrl ?? previous.WebUrl;
        previous.WebId = incoming.WebId ?? previous.WebId;
        previous.Url = incoming.Url ?? previous.Url;
        previous.FileName = incoming.FileName ?? previous.FileName;
        previous.PageType = incoming.PageType ?? previous.PageType;
        previous.ContentTypeId = incoming.ContentTypeId ?? previous.ContentTypeId;
        previous.CustomizedPageStatus = incoming.CustomizedPageStatus ?? previous.CustomizedPageStatus;
        previous.SetupPath = incoming.SetupPath ?? previous.SetupPath;
        if (incoming.FileReadState is not (null or ClassicAspxReadStates.NotRequested or ClassicAspxReadStates.NotAttempted))
        {
            previous.FileReadState = incoming.FileReadState;
        }
        previous.FileErrorCodes = Join(previous.FileErrorCodes, incoming.FileErrorCodes);
        previous.FileErrorDetail = Join(previous.FileErrorDetail, incoming.FileErrorDetail);
        var previousHasSource = previous.HandlerEffectiveType != null;
        var incomingHasSource = incoming.HandlerEffectiveType != null;
        if (previousHasSource && incomingHasSource && previous.HandlerEffectiveType != incoming.HandlerEffectiveType)
        {
            previous.CoverageState = ClassicAspxCoverageStates.ChangedDuringScan;
            previous.ProvenanceJson = UnionProvenance(previous.ProvenanceJson,
                JsonSerializer.Serialize(new[] { new { observation = "handler_changed", before = previous.HandlerEffectiveType, after = incoming.HandlerEffectiveType } }));
        }
        // A metadata-only/denied observation does not erase previously acquired source facts.
        if (incomingHasSource || !previousHasSource && incoming.HandlerReadState is not (null or ClassicAspxReadStates.NotRequested or ClassicAspxReadStates.NotAttempted))
        {
            previous.HandlerDeclaredType = incoming.HandlerDeclaredType;
            previous.HandlerNormalizedType = incoming.HandlerNormalizedType;
            previous.HandlerEffectiveType = incoming.HandlerEffectiveType;
            previous.HandlerReadState = incoming.HandlerReadState;
            previous.HandlerParseState = incoming.HandlerParseState;
            previous.HandlerSource = incoming.HandlerSource;
            previous.HandlerDefaultProvenance = incoming.HandlerDefaultProvenance;
            previous.HandlerReasonCode = incoming.HandlerReasonCode;
        }
        previous.HandlerErrorCodes = Join(previous.HandlerErrorCodes, incoming.HandlerErrorCodes);
        previous.HandlerErrorDetail = Join(previous.HandlerErrorDetail, incoming.HandlerErrorDetail);
        previous.FirstObservedAtUtc = Earliest(previous.FirstObservedAtUtc,
            incoming.FirstObservedAtUtc == default ? incoming.ObservedAtUtc : incoming.FirstObservedAtUtc);
        previous.ObservedAtUtc = Latest(previous.ObservedAtUtc, incoming.ObservedAtUtc);
    }

    /// <summary>Preserves change and incompleteness precedence while retaining the existing complete or fallback coverage label.</summary>
    internal static string MergeCoverage(string left, string right)
    {
        if (left == ClassicAspxCoverageStates.ChangedDuringScan || right == ClassicAspxCoverageStates.ChangedDuringScan)
        {
            return ClassicAspxCoverageStates.ChangedDuringScan;
        }
        if (left == ClassicAspxCoverageStates.Incomplete || right == ClassicAspxCoverageStates.Incomplete)
        {
            return ClassicAspxCoverageStates.Incomplete;
        }
        if (left == ClassicAspxCoverageStates.Complete || right == ClassicAspxCoverageStates.Complete)
        {
            return ClassicAspxCoverageStates.Complete;
        }
        return right ?? left ?? ClassicAspxCoverageStates.Unknown;
    }

    /// <summary>Combines JSON-array evidence as deterministically ordered, canonical, duplicate-free entries; non-array inputs are rejected.</summary>
    internal static string UnionProvenance(string left, string right)
    {
        var entries = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var json in new[] { left, right })
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? EmptyProvenance : json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new ArgumentException("Provenance must be a JSON array.");
            }
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                entries.Add(Canonical(entry));
            }
        }
        return "[" + string.Join(',', entries) + "]";
    }

    /// <summary>Orders object properties recursively while preserving array order and raw scalar JSON values for provenance equality.</summary>
    private static string Canonical(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(',', element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => JsonSerializer.Serialize(p.Name) + ":" + Canonical(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(',', element.EnumerateArray().Select(Canonical)) + "]",
        _ => element.GetRawText()
    };
    /// <summary>Joins nonblank error observations in first-seen order without duplicating identical ordinal text.</summary>
    private static string Join(string left, string right) => string.Join("\n", new[] { left, right }
        .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal));
    /// <summary>Reduces a locator to its unescaped, query-independent path for change detection, not identity creation.</summary>
    private static string CanonicalLocator(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            value = uri.AbsolutePath;
        }
        return Uri.UnescapeDataString(value.Split('?', '#')[0]).TrimEnd('/');
    }
    /// <summary>Retains the later observation timestamp when merging repeat evidence.</summary>
    private static DateTime Latest(DateTime left, DateTime right) => left > right ? left : right;
    /// <summary>Retains the earliest nondefault observation timestamp when merging repeat evidence.</summary>
    private static DateTime Earliest(DateTime left, DateTime right) => left == default ? right : right == default ? left : left < right ? left : right;
}
