using PnP.Scanning.Core.ClassicPages.Contracts.Page;
using PnP.Scanning.Core.ClassicPages.Analysis.Page;
#nullable enable
using System.Text.Json;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.ClassicPages.Compatibility.Legacy;

/// <summary>Preserves historical CSV classification, including retired Delve labels, outside the active ASPX and Posts scope.</summary>
internal static class ClassicAspxExportClassifier
{
    internal const string ItemMetadata = "ItemMetadata";
    internal const string FileOnlyFallback = "FileOnlyFallback";
    internal const string Unknown = "Unknown";
    private const string ModernSitePage = "ModernSitePage";
    private const string ClientSideApplicationIdField = "ClientSideApplicationId";
    private const string HtmlFileTypeField = "HTML_x0020_File_x0020_Type";
    private const string FileTypeField = "File_x0020_Type";
    private const string BsnField = "BSN";
    private const string WikiField = "WikiField";
    private const string Omitted = "omitted";
    private const string Null = "null";
    private const string Value = "value";

    /// <summary>Preserves the current publishing override and item-field priority, using only the existing content-type fallback when item metadata cannot be restored.</summary>
    internal static Classification Classify(SPFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.ItemMetadataReadState == ClassicAspxReadStates.Available && TryRestoreFields(file, out var fields))
        {
            if (ClassicPageRules.IsPublishingContentType(file.ContentTypeId))
            {
                return new Classification(
                    ClassicPageRules.PublishingPage,
                    ItemMetadata,
                    "ExistingPublishingContentType");
            }

            string pageType = ClassicPageRules.GetPageType(fields);
            if (pageType is ClassicPageRules.ASPXPage or ClassicPageRules.WikiPage &&
                (!fields.TryGetValue(WikiField, out object? wiki) || wiki == null) &&
                fields.TryGetValue(FileTypeField, out object? fileType) && string.Equals(
                    Convert.ToString(fileType, System.Globalization.CultureInfo.InvariantCulture),
                    "pointpub",
                    StringComparison.InvariantCultureIgnoreCase))
                pageType = ClassicPageRules.DelveBlogPage;
            return new Classification(pageType, ItemMetadata, "ExistingItemFieldHeuristics");
        }

        var inferred = SharePointLiveAspxDiscoveryProvider.InferPageType(file.ContentTypeId);
        return new Classification(
            inferred,
            inferred == null ? Unknown : FileOnlyFallback,
            file.ItemMetadataReadState == ClassicAspxReadStates.Available
                ? "ItemFieldSnapshotUnavailable;ExistingContentTypeFallback"
                : "ItemMetadataUnavailable;ExistingContentTypeFallback");
    }

    /// <summary>Restores omitted, explicit-null and value states using the original field names, without retaining or inventing page-source content.</summary>
    private static bool TryRestoreFields(SPFile file, out IDictionary<string, object?> fields)
    {
        fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(file.ItemMetadataFieldStatesJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var states = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String || !states.TryAdd(
                        property.Name,
                        property.Value.GetString()!))
                {
                    return false;
                }
            }

            return RestoreField(fields, states, ClientSideApplicationIdField, file.ClientSideApplicationId) &&
                   RestoreField(fields, states, HtmlFileTypeField, file.HtmlFileType) &&
                   RestoreField(fields, states, FileTypeField, file.FileType) &&
                   RestoreField(fields, states, BsnField, file.BSN) && RestoreField(
                       fields,
                       states,
                       WikiField,
                       file.WikiFieldHasValue == true ? string.Empty : null,
                       valueIsKnownNull: file.WikiFieldHasValue == false);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Reconstructs a single field exactly as observed; a missing or omitted field is never synthesized from a nullable column.</summary>
    private static bool RestoreField(
        IDictionary<string, object?> fields,
        IReadOnlyDictionary<string, string> states,
        string name,
        string? value,
        bool valueIsKnownNull = false)
    {
        if (!states.TryGetValue(name, out var state) || state == Omitted)
        {
            return true;
        }

        if (state == Null)
        {
            fields.Add(name, null);
            return true;
        }

        if (state == Value && (value != null || valueIsKnownNull))
        {
            fields.Add(name, value);
            return true;
        }

        return false;
    }

    /// <summary>Retains the existing classification result and identifies only the already-known modern result names for export exclusion.</summary>
    internal sealed record Classification(string? PageType, string State, string Reason)
    {
        internal bool IsModern => PageType is ClassicPageRules.ModernPage or ModernSitePage;
    }
}