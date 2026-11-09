#nullable enable
using System.Text.Json;
using PnP.Scanning.Core.Pipeline.Contracts.Module;

namespace PnP.Scanning.Core.Pipeline.Contracts.Shared;

internal static class ClassicPageSourceJson
{
    internal static T Convert<T>(object value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    internal static byte[] Bytes<T>(T value) => System.Text.Encoding.UTF8.GetBytes(VersionedJson.From(value).Json);
    internal static T Read<T>(byte[]? bytes) => bytes == null
        ? throw new SnapshotIntegrityException("A required Classic Page source artifact was not acquired.")
        : new VersionedJson(System.Text.Encoding.UTF8.GetString(bytes)).Value.Deserialize<T>() ?? throw new SnapshotIntegrityException("Classic Page source artifact is empty.");
    internal static string Kind(SourceRecord record) => record.Metadata.Value.GetProperty("kind").GetString()!;
    internal static string Kind(CollectedObservation record) => record.Metadata.Value.GetProperty("kind").GetString()!;
    internal static string WebKey(string siteUrl, string webUrl) => siteUrl.TrimEnd('/').ToLowerInvariant() + "|" + webUrl.ToLowerInvariant();
}
