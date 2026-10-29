using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Storage;
using System.Text.Json;

namespace PnP.Scanning.Core.Discovery.Aspx;

/// <summary>Atomically merges bounded physical-file observations without inventing identities or discarding conflicting evidence.</summary>
internal sealed class ClassicAspxProjectionWriter
{
    internal const int MaximumBatchSize = 256;
    private const string EmptyProvenance = "[]";
    private const string MetadataChangedObservation = "metadata_changed";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly Func<ScanContext> createContext;

    /// <summary>Creates a writer whose independently owned contexts target the specified assessment scan.</summary>
    internal ClassicAspxProjectionWriter(Guid scanId) : this(() => new ScanContext(scanId))
    {
    }

    /// <summary>Retains the context factory used to isolate each serialized transactional batch.</summary>
    internal ClassicAspxProjectionWriter(Func<ScanContext> createContext)
    {
        this.createContext = createContext ?? throw new ArgumentNullException(nameof(createContext));
    }

    /// <summary>Validates one bounded single-scan inventory batch, unions repeat evidence, and commits all observations or none.</summary>
    internal async Task WriteAsync(IEnumerable<ClassicAspxInventory> inventories, CancellationToken cancellationToken = default)
    {
        var files = Bounded(inventories);
        if (files.Length == 0)
        {
            return;
        }
        foreach (var file in files)
        {
            Validate(file);
        }
        var scan = files[0].ScanId;
        if (files.Any(row => row.ScanId != scan))
        {
            throw new ArgumentException("A projection batch belongs to exactly one scan.");
        }
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
            // Storage failures propagate; no part of the batch is reported as an acquisition gap or committed separately.
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Reads at most one batch plus a sentinel and rejects oversized input before creating a database context.</summary>
    private static ClassicAspxInventory[] Bounded(IEnumerable<ClassicAspxInventory> rows)
    {
        var result = (rows ?? Array.Empty<ClassicAspxInventory>()).Take(MaximumBatchSize + 1).ToArray();
        if (result.Length > MaximumBatchSize)
        {
            throw new ArgumentException("Projection batches must remain bounded.");
        }
        return result;
    }

    /// <summary>Requires a real scan and physical-file key plus JSON-array provenance before any batch can be persisted.</summary>
    private static void Validate(ClassicAspxInventory row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.ScanId == Guid.Empty)
        {
            throw new ArgumentException("An observed scan is required.");
        }
        var key = ClassicAspxInventory.CreateTenantFileKey(row.SiteCollectionId, row.FileUniqueId);
        if (!string.Equals(row.AspxKey, key, StringComparison.Ordinal))
        {
            throw new ArgumentException("ASPX key differs from observed physical-file identity.");
        }
        if (row.WebId == Guid.Empty || row.ListId == Guid.Empty || row.FolderUniqueId == Guid.Empty)
        {
            throw new ArgumentException("Optional observation GUIDs must be real or absent, never empty placeholders.");
        }
        _ = UnionProvenance(EmptyProvenance, row.ProvenanceJson);
    }

    /// <summary>Unions evidence and retains conflicting metadata in provenance while preserving acquired values when later observations omit them.</summary>
    private static void Merge(ClassicAspxInventory previous, ClassicAspxInventory incoming)
    {
        previous.ProvenanceJson = UnionProvenance(previous.ProvenanceJson, incoming.ProvenanceJson);
        previous.CoverageState = MergeCoverage(previous.CoverageState, incoming.CoverageState);
        previous.SiteUrl = MergeText(previous, nameof(previous.SiteUrl), previous.SiteUrl, incoming.SiteUrl, true);
        previous.WebUrl = MergeText(previous, nameof(previous.WebUrl), previous.WebUrl, incoming.WebUrl, true);
        previous.Url = MergeText(previous, nameof(previous.Url), previous.Url, incoming.Url, true);
        previous.WebId = MergeGuid(previous, nameof(previous.WebId), previous.WebId, incoming.WebId);
        previous.ListId = MergeGuid(previous, nameof(previous.ListId), previous.ListId, incoming.ListId);
        previous.FolderUniqueId = MergeGuid(previous, nameof(previous.FolderUniqueId), previous.FolderUniqueId, incoming.FolderUniqueId);
        previous.FileName = MergeText(previous, nameof(previous.FileName), previous.FileName, incoming.FileName);
        previous.ContentTypeId = MergeText(previous, nameof(previous.ContentTypeId), previous.ContentTypeId, incoming.ContentTypeId);
        previous.CustomizedPageStatus = MergeText(previous, nameof(previous.CustomizedPageStatus), previous.CustomizedPageStatus, incoming.CustomizedPageStatus);
        previous.SetupPath = MergeText(previous, nameof(previous.SetupPath), previous.SetupPath, incoming.SetupPath);
        if (incoming.FileReadState is not (null or ClassicAspxReadStates.NotRequested or ClassicAspxReadStates.NotAttempted))
        {
            if (previous.FileReadState == ClassicAspxReadStates.Available && incoming.FileReadState != ClassicAspxReadStates.Available)
            {
                RecordConflict(previous, nameof(previous.FileReadState), previous.FileReadState, incoming.FileReadState);
            }
            else
            {
                previous.FileReadState = incoming.FileReadState;
            }
        }
        previous.FileErrorCodes = Join(previous.FileErrorCodes, incoming.FileErrorCodes);
        previous.FileErrorDetail = Join(previous.FileErrorDetail, incoming.FileErrorDetail);
        previous.FirstObservedAtUtc = Earliest(previous.FirstObservedAtUtc,
            incoming.FirstObservedAtUtc == default ? incoming.ObservedAtUtc : incoming.FirstObservedAtUtc);
        previous.ObservedAtUtc = Latest(previous.ObservedAtUtc, incoming.ObservedAtUtc);
    }

    /// <summary>Merges optional text and records both conflicting facts; equivalent URL paths do not falsely signal a location change.</summary>
    private static string MergeText(ClassicAspxInventory row, string field, string previous, string incoming, bool locator = false)
    {
        if (incoming == null)
        {
            return previous;
        }
        var equal = locator
            ? string.Equals(CanonicalLocator(previous), CanonicalLocator(incoming), StringComparison.OrdinalIgnoreCase)
            : string.Equals(previous, incoming, StringComparison.Ordinal);
        if (previous != null && !equal)
        {
            RecordConflict(row, field, previous, incoming);
        }
        return incoming;
    }

    /// <summary>Merges an optional observation GUID without changing the physical-file key and records conflicting ownership evidence.</summary>
    private static Guid? MergeGuid(ClassicAspxInventory row, string field, Guid? previous, Guid? incoming)
    {
        if (previous.HasValue && incoming.HasValue && previous != incoming)
        {
            RecordConflict(row, field, previous, incoming);
        }
        return incoming ?? previous;
    }

    /// <summary>Marks scan-time metadata disagreement and retains both values as deterministic unioned evidence.</summary>
    private static void RecordConflict(ClassicAspxInventory row, string field, object before, object after)
    {
        row.CoverageState = ClassicAspxCoverageStates.ChangedDuringScan;
        row.ProvenanceJson = UnionProvenance(row.ProvenanceJson,
            JsonSerializer.Serialize(new[] { new { observation = MetadataChangedObservation, field, before, after } }));
    }

    /// <summary>Preserves change and incompleteness precedence without promoting observed files to complete tenant coverage.</summary>
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
        if (left == ClassicAspxCoverageStates.Observed || right == ClassicAspxCoverageStates.Observed)
        {
            return ClassicAspxCoverageStates.Observed;
        }
        return right ?? left ?? ClassicAspxCoverageStates.Unknown;
    }

    /// <summary>Combines JSON-array evidence as deterministically ordered, canonical, duplicate-free entries and rejects non-array inputs.</summary>
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

    /// <summary>Orders object properties recursively while preserving array order and raw scalar JSON values for evidence equality.</summary>
    private static string Canonical(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(',', element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => JsonSerializer.Serialize(p.Name) + ":" + Canonical(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(',', element.EnumerateArray().Select(Canonical)) + "]",
        _ => element.GetRawText()
    };

    /// <summary>Preserves null, empty, and multiline error blocks while avoiding duplicate blocks when observations are replayed.</summary>
    private static string Join(string left, string right)
    {
        if (left == null)
        {
            return right;
        }
        if (right == null || string.Equals(left, right, StringComparison.Ordinal) ||
            left.StartsWith(right + "\n", StringComparison.Ordinal) ||
            left.EndsWith("\n" + right, StringComparison.Ordinal) ||
            left.Contains("\n" + right + "\n", StringComparison.Ordinal))
        {
            return left;
        }
        return left + "\n" + right;
    }

    /// <summary>Reduces an observed locator to its unescaped query-independent path for comparison, never for identity creation.</summary>
    private static string CanonicalLocator(string value)
    {
        if (value == null)
        {
            return null;
        }
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            value = uri.AbsolutePath;
        }
        return Uri.UnescapeDataString(value.Split('?', '#')[0]).TrimEnd('/');
    }

    /// <summary>Retains the latest observation timestamp across replayed evidence.</summary>
    private static DateTime Latest(DateTime left, DateTime right) => left > right ? left : right;

    /// <summary>Retains the earliest nondefault observation timestamp across replayed evidence.</summary>
    private static DateTime Earliest(DateTime left, DateTime right) => left == default ? right : right == default ? left : left < right ? left : right;
}
