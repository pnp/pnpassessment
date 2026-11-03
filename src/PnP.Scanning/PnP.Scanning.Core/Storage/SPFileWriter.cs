using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace PnP.Scanning.Core.Storage;

/// <summary>Atomically merges bounded physical-file observations without inventing identities or discarding conflicting evidence.</summary>
internal sealed class SPFileWriter
{
    internal const int MaximumBatchSize = 256;
    private const string EmptyProvenance = "[]";
    private const string EmptyFieldStates = "{}";
    private const string FieldOmitted = "omitted";
    private const string FieldNull = "null";
    private const string FieldValue = "value";
    private const string ClientSideApplicationIdField = "ClientSideApplicationId";
    private const string HtmlFileTypeField = "HTML_x0020_File_x0020_Type";
    private const string FileTypeField = "File_x0020_Type";
    private const string BsnField = "BSN";
    private const string ContentTypeIdField = "ContentTypeId";
    private const string WikiField = "WikiField";
    private const string NoExecuteField = "NoExecute";
    private const string NoExecuteNullReadState = "Null";
    private const string MetadataChangedObservation = "metadata_changed";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly Func<ScanContext> createContext;

    /// <summary>Creates a writer whose independently owned contexts target the specified assessment scan.</summary>
    internal SPFileWriter(Guid scanId) : this(() => new ScanContext(scanId))
    {
    }

    /// <summary>Retains the context factory used to isolate each serialized transactional batch.</summary>
    internal SPFileWriter(Func<ScanContext> createContext)
    {
        this.createContext = createContext ?? throw new ArgumentNullException(nameof(createContext));
    }

    /// <summary>Validates one bounded single-scan raw-file batch, unions repeat evidence, and commits all observations or none.</summary>
    internal async Task WriteAsync(IEnumerable<SPFile> observations, CancellationToken cancellationToken = default)
    {
        var files = Bounded(observations);
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
            throw new ArgumentException("A raw-file batch belongs to exactly one scan.");
        }
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var db = createContext();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            foreach (var observation in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var previous = await db.SPFiles.FindAsync(
                    new object[] { observation.SiteCollectionId, observation.FileUniqueId }, cancellationToken).ConfigureAwait(false);
                if (previous == null)
                {
                    observation.ProvenanceJson = UnionProvenance(EmptyProvenance, observation.ProvenanceJson);
                    var itemStates = ReadFieldStates(observation.ItemMetadataFieldStatesJson);
                    NormalizeInitialItemFacts(observation, itemStates);
                    observation.ItemMetadataFieldStatesJson = JsonSerializer.Serialize(itemStates);
                    if (observation.FirstObservedAtUtc == default)
                    {
                        observation.FirstObservedAtUtc = observation.ObservedAtUtc;
                    }
                    db.SPFiles.Add(observation);
                }
                else
                {
                    if (previous.ScanId != observation.ScanId)
                    {
                        throw new InvalidOperationException("The physical file already belongs to a different scan.");
                    }
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
    private static SPFile[] Bounded(IEnumerable<SPFile> rows)
    {
        var result = (rows ?? Array.Empty<SPFile>()).Take(MaximumBatchSize + 1).ToArray();
        if (result.Length > MaximumBatchSize)
        {
            throw new ArgumentException("Raw-file batches must remain bounded.");
        }
        return result;
    }

    /// <summary>Requires real scan/file GUIDs and valid evidence/state JSON before opening storage for any batch member.</summary>
    private static void Validate(SPFile row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.ScanId == Guid.Empty)
        {
            throw new ArgumentException("An observed scan is required.");
        }
        if (row.SiteCollectionId == Guid.Empty || row.FileUniqueId == Guid.Empty)
        {
            throw new ArgumentException("Physical identity requires observed nonempty site-collection and file GUIDs.");
        }
        if (row.WebId == Guid.Empty || row.ListId == Guid.Empty || row.FolderUniqueId == Guid.Empty)
        {
            throw new ArgumentException("Optional observation GUIDs must be real or absent, never empty placeholders.");
        }
        _ = UnionProvenance(EmptyProvenance, row.ProvenanceJson);
        var states = ReadFieldStates(row.ItemMetadataFieldStatesJson);
        ValidateItemValue(states, ClientSideApplicationIdField, row.ClientSideApplicationId);
        ValidateItemValue(states, HtmlFileTypeField, row.HtmlFileType);
        ValidateItemValue(states, FileTypeField, row.FileType);
        ValidateItemValue(states, BsnField, row.BSN);
        ValidateItemValue(states, ContentTypeIdField, row.ContentTypeId);
        ValidateItemValue(states, WikiField, row.WikiFieldHasValue);
        ValidateItemValue(states, NoExecuteField, row.NoExecuteRaw);
    }

    /// <summary>Unions evidence and raw metadata, including uninterpreted NoExecute text; retains conflicts without deriving a full document-flags mask.</summary>
    private static void Merge(SPFile previous, SPFile incoming)
    {
        previous.ProvenanceJson = UnionProvenance(previous.ProvenanceJson, incoming.ProvenanceJson);
        previous.CoverageState = MergeCoverage(previous.CoverageState, incoming.CoverageState);
        previous.SiteUrl = MergeText(previous, nameof(previous.SiteUrl), previous.SiteUrl, incoming.SiteUrl);
        previous.WebUrl = MergeText(previous, nameof(previous.WebUrl), previous.WebUrl, incoming.WebUrl);
        previous.ServerRelativeUrl = MergeText(previous, nameof(previous.ServerRelativeUrl), previous.ServerRelativeUrl, incoming.ServerRelativeUrl);
        previous.WebId = MergeValue(previous, nameof(previous.WebId), previous.WebId, incoming.WebId);
        previous.ListId = MergeValue(previous, nameof(previous.ListId), previous.ListId, incoming.ListId);
        previous.FolderUniqueId = MergeValue(previous, nameof(previous.FolderUniqueId), previous.FolderUniqueId, incoming.FolderUniqueId);
        previous.ListItemId = MergeValue(previous, nameof(previous.ListItemId), previous.ListItemId, incoming.ListItemId);
        previous.Length = MergeValue(previous, nameof(previous.Length), previous.Length, incoming.Length);
        previous.FileName = MergeText(previous, nameof(previous.FileName), previous.FileName, incoming.FileName);
        var itemStates = ReadFieldStates(incoming.ItemMetadataFieldStatesJson);
        previous.ContentTypeId = MergeItemText(previous, nameof(previous.ContentTypeId), ContentTypeIdField, previous.ContentTypeId, incoming.ContentTypeId, itemStates);
        previous.CustomizedPageStatus = MergeText(previous, nameof(previous.CustomizedPageStatus), previous.CustomizedPageStatus, incoming.CustomizedPageStatus);
        previous.SetupPath = MergeText(previous, nameof(previous.SetupPath), previous.SetupPath, incoming.SetupPath);
        previous.ClientSideApplicationId = MergeItemText(previous, nameof(previous.ClientSideApplicationId), ClientSideApplicationIdField, previous.ClientSideApplicationId, incoming.ClientSideApplicationId, itemStates);
        previous.HtmlFileType = MergeItemText(previous, nameof(previous.HtmlFileType), HtmlFileTypeField, previous.HtmlFileType, incoming.HtmlFileType, itemStates);
        previous.FileType = MergeItemText(previous, nameof(previous.FileType), FileTypeField, previous.FileType, incoming.FileType, itemStates);
        previous.BSN = MergeItemText(previous, nameof(previous.BSN), BsnField, previous.BSN, incoming.BSN, itemStates);
        if (!itemStates.TryGetValue(WikiField, out var wikiState) || wikiState != FieldOmitted)
        {
            var wikiValue = wikiState == FieldNull ? false : incoming.WikiFieldHasValue;
            previous.WikiFieldHasValue = MergeValue(previous, nameof(previous.WikiFieldHasValue), previous.WikiFieldHasValue, wikiValue);
        }
        MergeNoExecute(previous, incoming, itemStates);
        previous.FileReadState = MergeReadState(previous, nameof(previous.FileReadState), previous.FileReadState, incoming.FileReadState);
        previous.ItemMetadataReadState = MergeReadState(previous, nameof(previous.ItemMetadataReadState), previous.ItemMetadataReadState, incoming.ItemMetadataReadState);
        previous.ItemMetadataFieldStatesJson = MergeFieldStates(previous, incoming.ItemMetadataFieldStatesJson);
        previous.FileErrorCodes = Join(previous.FileErrorCodes, incoming.FileErrorCodes);
        previous.FileErrorDetail = Join(previous.FileErrorDetail, incoming.FileErrorDetail);
        previous.FirstObservedAtUtc = Earliest(previous.FirstObservedAtUtc,
            incoming.FirstObservedAtUtc == default ? incoming.ObservedAtUtc : incoming.FirstObservedAtUtc);
        previous.ObservedAtUtc = Latest(previous.ObservedAtUtc, incoming.ObservedAtUtc);
    }

    /// <summary>Preserves omitted NoExecute facts, clears explicit null with before/after evidence, and retains the caller-observed source and read outcome.</summary>
    private static void MergeNoExecute(SPFile previous, SPFile incoming, IReadOnlyDictionary<string, string> states)
    {
        states.TryGetValue(NoExecuteField, out var state);
        if (state != FieldOmitted)
        {
            if (state == FieldValue && previous.NoExecuteRaw == null && previous.NoExecuteReadState == NoExecuteNullReadState)
            {
                RecordConflict(previous, nameof(previous.NoExecuteRaw), null, incoming.NoExecuteRaw);
            }
            previous.NoExecuteRaw = MergeItemText(previous, nameof(previous.NoExecuteRaw), NoExecuteField, previous.NoExecuteRaw, incoming.NoExecuteRaw, states);
            previous.NoExecuteSource = MergeText(previous, nameof(previous.NoExecuteSource), previous.NoExecuteSource, incoming.NoExecuteSource);
        }
        previous.NoExecuteReadState = MergeNoExecuteReadState(previous, incoming.NoExecuteReadState, state);
    }

    /// <summary>Accepts actual NoExecute value/null outcomes while retaining both known null and known value facts when later attempts omit or fail.</summary>
    private static string MergeNoExecuteReadState(SPFile row, string incoming, string fieldState)
    {
        var previous = row.NoExecuteReadState;
        var returned = fieldState != FieldOmitted &&
            ((fieldState is FieldNull or FieldValue) || (incoming is ClassicAspxReadStates.Available or NoExecuteNullReadState));
        if (returned)
        {
            if ((previous is ClassicAspxReadStates.Available or NoExecuteNullReadState) &&
                !string.Equals(previous, incoming, StringComparison.Ordinal))
            {
                RecordConflict(row, nameof(row.NoExecuteReadState), previous, incoming);
            }
            return incoming;
        }
        if (previous == NoExecuteNullReadState)
        {
            if (incoming is not (null or ClassicAspxReadStates.NotRequested or ClassicAspxReadStates.NotAttempted or NoExecuteNullReadState))
            {
                RecordConflict(row, nameof(row.NoExecuteReadState), previous, incoming);
            }
            return previous;
        }
        return MergeReadState(row, nameof(row.NoExecuteReadState), previous, incoming);
    }

    /// <summary>Requires known fields marked as returned values to carry their corresponding raw text or boolean fact.</summary>
    private static void ValidateItemValue(IReadOnlyDictionary<string, string> states, string field, object value)
    {
        if (states.TryGetValue(field, out var state) && state == FieldValue && value == null)
        {
            throw new ArgumentException("A returned item metadata value requires its corresponding raw fact: " + field);
        }
    }

    /// <summary>Applies explicit null and omission to initial known item fields without inventing returned values or interpreting unknown field names.</summary>
    private static void NormalizeInitialItemFacts(SPFile row, IReadOnlyDictionary<string, string> states)
    {
        row.ClientSideApplicationId = InitialItemText(ClientSideApplicationIdField, row.ClientSideApplicationId, states);
        row.HtmlFileType = InitialItemText(HtmlFileTypeField, row.HtmlFileType, states);
        row.FileType = InitialItemText(FileTypeField, row.FileType, states);
        row.BSN = InitialItemText(BsnField, row.BSN, states);
        row.ContentTypeId = InitialItemText(ContentTypeIdField, row.ContentTypeId, states);
        row.NoExecuteRaw = InitialItemText(NoExecuteField, row.NoExecuteRaw, states);
        if (states.TryGetValue(WikiField, out var state))
        {
            row.WikiFieldHasValue = state switch
            {
                FieldNull => false,
                FieldOmitted => null,
                _ => row.WikiFieldHasValue
            };
        }
    }

    /// <summary>Leaves an initial known text fact absent when the API omitted it or explicitly returned null.</summary>
    private static string InitialItemText(string key, string value, IReadOnlyDictionary<string, string> states)
    {
        return states.TryGetValue(key, out var state) && state is FieldNull or FieldOmitted ? null : value;
    }

    /// <summary>Preserves omitted known item text, clears explicit null with before/after evidence, and merges actual returned values.</summary>
    private static string MergeItemText(SPFile row, string field, string key, string previous, string incoming, IReadOnlyDictionary<string, string> states)
    {
        if (states.TryGetValue(key, out var state))
        {
            if (state == FieldOmitted)
            {
                return previous;
            }
            if (state == FieldNull)
            {
                if (previous != null)
                {
                    RecordConflict(row, field, previous, null);
                }
                return null;
            }
        }
        return MergeText(row, field, previous, incoming);
    }

    /// <summary>Merges raw optional text without decoding or fabricating locators and retains both conflicting values.</summary>
    private static string MergeText(SPFile row, string field, string previous, string incoming)
    {
        if (incoming == null)
        {
            return previous;
        }
        if (previous != null && !string.Equals(previous, incoming, StringComparison.Ordinal))
        {
            RecordConflict(row, field, previous, incoming);
        }
        return incoming;
    }

    /// <summary>Merges an optional raw scalar, preserving acquired facts on omission and recording both conflicting observations.</summary>
    private static T? MergeValue<T>(SPFile row, string field, T? previous, T? incoming) where T : struct
    {
        if (previous.HasValue && incoming.HasValue && !EqualityComparer<T>.Default.Equals(previous.Value, incoming.Value))
        {
            RecordConflict(row, field, previous.Value, incoming.Value);
        }
        return incoming ?? previous;
    }

    /// <summary>Retains successful metadata on omitted/failed reads while preserving later failure outcomes as conflicting evidence.</summary>
    private static string MergeReadState(SPFile row, string field, string previous, string incoming)
    {
        if (incoming is null or ClassicAspxReadStates.NotRequested or ClassicAspxReadStates.NotAttempted)
        {
            return previous;
        }
        if (previous == ClassicAspxReadStates.Available && incoming != ClassicAspxReadStates.Available)
        {
            RecordConflict(row, field, previous, incoming);
            return previous;
        }
        return incoming;
    }

    /// <summary>Combines per-field read outcomes, retaining prior states on omission and preserving explicit null/value transitions as conflicts.</summary>
    private static string MergeFieldStates(SPFile row, string incomingJson)
    {
        var states = ReadFieldStates(row.ItemMetadataFieldStatesJson);
        foreach (var incoming in ReadFieldStates(incomingJson))
        {
            if (states.TryGetValue(incoming.Key, out var previous))
            {
                if (incoming.Value == FieldOmitted)
                {
                    continue;
                }
                if (previous != FieldOmitted && previous != incoming.Value)
                {
                    RecordConflict(row, nameof(SPFile.ItemMetadataFieldStatesJson) + "." + incoming.Key, previous, incoming.Value);
                }
            }
            states[incoming.Key] = incoming.Value;
        }
        return JsonSerializer.Serialize(states);
    }

    /// <summary>Validates an exact field-name-to-omitted/null/value JSON object and deterministically orders its unchanged field names.</summary>
    private static SortedDictionary<string, string> ReadFieldStates(string json)
    {
        using var document = JsonDocument.Parse(json ?? EmptyFieldStates);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Item metadata field states must be a JSON object.");
        }
        var states = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in document.RootElement.EnumerateObject())
        {
            if (string.IsNullOrEmpty(field.Name) || field.Value.ValueKind != JsonValueKind.String ||
                field.Value.GetString() is not (FieldOmitted or FieldNull or FieldValue) ||
                !states.TryAdd(field.Name, field.Value.GetString()))
            {
                throw new ArgumentException("Each item metadata field must have one omitted, null, or value state.");
            }
        }
        return states;
    }

    /// <summary>Marks scan-time metadata disagreement and retains both values as deterministic unioned evidence.</summary>
    private static void RecordConflict(SPFile row, string field, object before, object after)
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

    /// <summary>Retains the latest observation timestamp across replayed evidence.</summary>
    private static DateTime Latest(DateTime left, DateTime right)
    {
        return left > right ? left : right;
    }

    /// <summary>Retains the earliest nondefault observation timestamp across replayed evidence.</summary>
    private static DateTime Earliest(DateTime left, DateTime right)
    {
        if (left == default)
        {
            return right;
        }
        return right == default || left < right ? left : right;
    }
}
