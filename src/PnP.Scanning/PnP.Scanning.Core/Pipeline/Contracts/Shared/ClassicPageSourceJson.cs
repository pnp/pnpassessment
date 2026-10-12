#nullable enable
using System.Text.Json;
using PnP.Scanning.Core.Pipeline.Contracts.Module;

namespace PnP.Scanning.Core.Pipeline.Contracts.Shared;

/// <summary>
/// Serializes versioned Classic Pages inputs and constructs stable source kind and web identity keys.
/// </summary>
internal static class ClassicPageSourceJson
{
    /// <summary>
    /// Copies a compatible object into the detached contract type through JSON serialization.
    /// </summary>
    internal static T Convert<T>(object value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    /// <summary>
    /// Encodes a typed source payload as UTF-8 bytes inside the versioned JSON envelope.
    /// </summary>
    internal static byte[] Bytes<T>(T value) => System.Text.Encoding.UTF8.GetBytes(VersionedJson.From(value).Json);
    /// <summary>
    /// Requires acquired source bytes and deserializes the versioned payload, rejecting missing or empty required evidence.
    /// </summary>
    internal static T Read<T>(byte[]? bytes) => bytes == null
        ? throw new SnapshotIntegrityException("A required Classic Page source artifact was not acquired.")
        : new VersionedJson(System.Text.Encoding.UTF8.GetString(bytes)).Value.Deserialize<T>() ?? throw new SnapshotIntegrityException("Classic Page source artifact is empty.");
    /// <summary>
    /// Reads the persisted source kind used to dispatch collection receipts and sealed observations.
    /// </summary>
    internal static string Kind(SourceRecord record) => record.Metadata.Value.GetProperty("kind").GetString()!;
    /// <summary>
    /// Reads the persisted source kind used to dispatch collection receipts and sealed observations.
    /// </summary>
    internal static string Kind(CollectedObservation record) => record.Metadata.Value.GetProperty("kind").GetString()!;
    /// <summary>
    /// Normalizes site and web URLs into the shared key used to join captured inputs and analysis projections.
    /// </summary>
    internal static string WebKey(string siteUrl, string webUrl) => siteUrl.TrimEnd('/').ToLowerInvariant() + "|" + webUrl.ToLowerInvariant();
}
