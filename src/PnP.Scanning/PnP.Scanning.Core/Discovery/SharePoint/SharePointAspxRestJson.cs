using System.Text.Json;

namespace PnP.Scanning.Core.Discovery;

/// <summary>Reads the optional, primitive SharePoint REST fields used by ASPX acquisition without inventing identities.</summary>
internal static class SharePointAspxRestJson
{
    /// <summary>Distinguishes a server-observed nonempty GUID from an absent or all-zero identifier.</summary>
    internal static bool HasNonEmptyGuid(Guid? identifier)
    {
        return identifier.HasValue && identifier.Value != Guid.Empty;
    }

    /// <summary>Checks field presence independently of a field's loaded null or empty value.</summary>
    internal static bool TryReadProperty(JsonElement row, string name, out JsonElement value)
    {
        return PnPContextSharePointAspxRestClient.TryProperty(row, name, out value);
    }

    /// <summary>Reads an optional REST string using the adapter's existing envelope/property semantics.</summary>
    internal static string ReadOptionalString(JsonElement row, string name)
    {
        return PnPContextSharePointAspxRestClient.String(row, name);
    }

    /// <summary>Reads an integer when supplied; admission of a positive Item ID remains the caller's responsibility.</summary>
    internal static int? ReadOptionalInt32(JsonElement row, string name)
    {
        return int.TryParse(ReadOptionalString(row, name), out int value) ? value : null;
    }

    /// <summary>Reads only a real nonempty GUID, leaving missing supplementary Item GUIDs nullable.</summary>
    internal static Guid? ReadNonEmptyGuid(JsonElement row, string name)
    {
        return Guid.TryParse(ReadOptionalString(row, name), out var identifier) && identifier != Guid.Empty ? identifier : null;
    }

    /// <summary>Supports both REST content-type strings and the StringValue object representation.</summary>
    internal static string ReadContentTypeId(JsonElement row, string name)
    {
        return TryReadProperty(row, name, out var value) && value.ValueKind == JsonValueKind.Object
            ? ReadOptionalString(value, SharePointAspxRestFields.ContentTypeStringValue)
            : ReadOptionalString(row, name);
    }

    /// <summary>Supports an explicit URL-field object or a primitive URL value without resolving or fetching it.</summary>
    internal static string ReadUrlFieldValue(JsonElement row, string name)
    {
        return TryReadProperty(row, name, out var value) && value.ValueKind == JsonValueKind.Object
            ? ReadOptionalString(value, SharePointAspxRestFields.Url)
            : ReadOptionalString(row, name);
    }
}
