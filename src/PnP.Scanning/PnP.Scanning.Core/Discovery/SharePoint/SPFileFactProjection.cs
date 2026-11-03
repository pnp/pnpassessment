using PnP.Scanning.Core.Storage;
using System.Globalization;
using System.Text.Json;

namespace PnP.Scanning.Core.Discovery;

/// <summary>Copies physical file and selected item metadata facts without deciding whether a file is Classic or poisoned.</summary>
internal static class SPFileFactProjection
{
    private const string ContentTypeField = "ContentTypeId";
    private const string ClientApplicationField = "ClientSideApplicationId";
    private const string HtmlTypeField = "HTML_x0020_File_x0020_Type";
    private const string FileTypeField = "File_x0020_Type";
    private const string BsnField = "BSN";
    private const string WikiField = "WikiField";
    private const string NoExecuteField = "NoExecute";
    private const string NoExecuteSource = "SPO.ListItem.NoExecute";
    private const string ValueState = "value";
    private const string NullState = "null";
    private const string OmittedState = "omitted";
    private static readonly string[] ItemFields = { ContentTypeField, ClientApplicationField, HtmlTypeField, FileTypeField, BsnField, WikiField, NoExecuteField };

    /// <summary>Admits only genuine physical file GUIDs and preserves every file extension returned by a folder batch.</summary>
    internal static SPFile FromPhysicalRecord(Guid scanId, RawDiscoveryRecord record)
    {
        if (!record.LocatorIsGuaranteedPhysicalFilePath || record.SiteCollectionId is not { } siteId || siteId == Guid.Empty ||
            !Guid.TryParse(record.FileUniqueId, out var fileId) || fileId == Guid.Empty)
        {
            return null;
        }
        var observed = DateTime.UtcNow;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ContentTypeField] = record.ContentTypeId == null ? OmittedState : ValueState
        };
        return new SPFile
        {
            ScanId = scanId,
            SiteCollectionId = siteId,
            FileUniqueId = fileId,
            WebId = RealOrNull(record.WebId),
            ListId = RealOrNull(record.ListId),
            FolderUniqueId = RealOrNull(record.FolderUniqueId),
            ListItemId = record.ListItemId is > 0 ? record.ListItemId : null,
            SiteUrl = record.SiteUrl,
            WebUrl = record.WebUrl,
            ServerRelativeUrl = DecodedServerRelativePath(record.PhysicalLocator),
            FileName = record.FileName,
            Length = record.Metadata != null && record.Metadata.TryGetValue("length", out var length) &&
                long.TryParse(length, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) && size >= 0 ? size : null,
            ContentTypeId = record.ContentTypeId,
            CustomizedPageStatus = record.CustomizedPageStatusRaw,
            SetupPath = record.SetupPath,
            FileReadState = "Complete",
            ItemMetadataReadState = record.ListItemId is > 0 ? "Partial" : "NotRequested",
            ItemMetadataFieldStatesJson = JsonSerializer.Serialize(fields),
            NoExecuteReadState = "NotRequested",
            ProvenanceJson = JsonSerializer.Serialize(new[] { new
            {
                source = "FolderFileObservation",
                record.SourceObjectId,
                record.ObservationMethod,
                metadata = record.Metadata
            }}),
            CoverageState = ClassicAspxCoverageStates.Observed,
            FirstObservedAtUtc = observed,
            ObservedAtUtc = observed
        };
    }

    /// <summary>Retains the exact fields already returned for selected-page assessment, without copying Wiki HTML or running classification.</summary>
    internal static SPFile FromItemMetadata(ClassicPageDiscovery page, IDictionary<string, object> values)
    {
        var result = FromDiscoveredFile(page);
        if (result == null)
        {
            return null;
        }
        var states = ItemFields.ToDictionary(name => name,
            name => !values.TryGetValue(name, out var value) ? OmittedState : value == null ? NullState : ValueState,
            StringComparer.Ordinal);
        result.ContentTypeId = ReadScalar(values, ContentTypeField);
        result.ClientSideApplicationId = ReadScalar(values, ClientApplicationField);
        result.HtmlFileType = ReadScalar(values, HtmlTypeField);
        result.FileType = ReadScalar(values, FileTypeField);
        result.BSN = ReadScalar(values, BsnField);
        result.WikiFieldHasValue = values.TryGetValue(WikiField, out var wiki) ? wiki != null : null;
        if (!values.TryGetValue(NoExecuteField, out var noExecute))
        {
            result.NoExecuteReadState = "NotReturned";
        }
        else
        {
            result.NoExecuteSource = NoExecuteSource;
            result.NoExecuteReadState = noExecute == null ? "Null" : noExecute is string ? "Available" : "InvalidType";
            result.NoExecuteRaw = noExecute is string raw ? raw : noExecute == null ? null : JsonSerializer.Serialize(noExecute);
        }
        result.ItemMetadataReadState = "Available";
        result.ItemMetadataFieldStatesJson = JsonSerializer.Serialize(states);
        result.ProvenanceJson = JsonSerializer.Serialize(new[] { new
        {
            source = "SelectedPageItemMetadata",
            page.RecordKey,
            fields = states
        }});
        return result;
    }

    /// <summary>Retains an item read failure separately from the already observed physical file and never changes its identity.</summary>
    internal static SPFile FromItemFailure(ClassicPageDiscovery page, Exception error)
    {
        var result = FromDiscoveredFile(page);
        if (result != null)
        {
            result.ItemMetadataReadState = "Failed";
            result.FileErrorCodes = AssessmentWebDiscovery.ErrorCode(error);
            result.FileErrorDetail = AssessmentWebDiscovery.ErrorDetail(error);
            result.ProvenanceJson = JsonSerializer.Serialize(new[] { new { source = "SelectedPageItemMetadataFailure", page.RecordKey } });
        }
        return result;
    }

    /// <summary>Builds a metadata-only update for a physical file already persisted by the discovery batch.</summary>
    private static SPFile FromDiscoveredFile(ClassicPageDiscovery page)
    {
        if (page.SiteCollectionId is not { } siteId || siteId == Guid.Empty ||
            page.FileUniqueId is not { } fileId || fileId == Guid.Empty)
        {
            return null;
        }
        return new SPFile
        {
            ScanId = page.ScanId,
            SiteCollectionId = siteId,
            FileUniqueId = fileId,
            WebId = RealOrNull(page.WebId),
            ListId = RealOrNull(page.ListId),
            FolderUniqueId = RealOrNull(page.FolderUniqueId),
            ListItemId = page.ListItemId is > 0 ? page.ListItemId : null,
            FileReadState = "NotRequested",
            ItemMetadataReadState = "NotRequested",
            NoExecuteReadState = "NotRequested",
            CoverageState = ClassicAspxCoverageStates.Observed,
            ObservedAtUtc = DateTime.UtcNow
        };
    }

    /// <summary>Preserves the native scalar text returned by metadata instead of parsing a business meaning at collection time.</summary>
    private static string ReadScalar(IDictionary<string, object> values, string name) =>
        values.TryGetValue(name, out var value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;

    /// <summary>Leaves raw server-relative percent/hash sequences untouched while removing only an absolute locator's authority prefix.</summary>
    private static string DecodedServerRelativePath(string locator)
    {
        if (string.IsNullOrEmpty(locator) || locator.StartsWith("/", StringComparison.Ordinal))
        {
            return locator;
        }
        int scheme = locator.IndexOf("://", StringComparison.Ordinal);
        int path = scheme < 0 ? -1 : locator.IndexOf('/', scheme + 3);
        return path < 0 ? locator : locator[path..];
    }

    /// <summary>Retains only observed nonempty supplementary GUIDs, without substituting a worker or generating an identity.</summary>
    private static Guid? RealOrNull(Guid? value) => value.HasValue && value.Value != Guid.Empty ? value : null;
}
