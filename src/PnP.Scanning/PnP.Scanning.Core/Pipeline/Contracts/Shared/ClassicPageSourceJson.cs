#nullable enable
using System.Text.Json;
using PnP.Scanning.Core.Pipeline.Contracts.Module;

namespace PnP.Scanning.Core.Pipeline.Contracts.Shared;

/// <summary>
/// Serializes detached source contracts in versioned envelopes and normalizes scope keys.
/// </summary>
internal static class ClassicPageSourceJson
{
    /// <summary>
    /// Copies matching contract properties through detached JSON serialization.
    /// </summary>
    internal static T Convert<T>(object value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;

    /// <summary>
    /// Encodes a typed source value in its versioned JSON envelope.
    /// </summary>
    internal static byte[] Bytes<T>(T value) => System.Text.Encoding.UTF8.GetBytes(VersionedJson.From(value).Json);

    /// <summary>
    /// Deserializes a required versioned source artifact and rejects absent content.
    /// </summary>
    internal static T Read<T>(byte[]? bytes) =>
        bytes == null
            ? throw new SnapshotIntegrityException("A required Classic Page source artifact was not acquired.")
            : new VersionedJson(System.Text.Encoding.UTF8.GetString(bytes)).Value.Deserialize<T>() ??
              throw new SnapshotIntegrityException("Classic Page source artifact is empty.");

    /// <summary>
    /// Reads the persisted source-kind discriminator from observation metadata.
    /// </summary>
    internal static string Kind(SourceRecord record) => record.Metadata.Value.GetProperty("kind").GetString()!;

    /// <summary>
    /// Reads the persisted source-kind discriminator from observation metadata.
    /// </summary>
    internal static string Kind(CollectedObservation record) => record.Metadata.Value.GetProperty("kind").GetString()!;

    /// <summary>
    /// Builds a stable case-insensitive site/Web scope key without changing native file identity.
    /// </summary>
    internal static string WebKey(string siteUrl, string webUrl) =>
        siteUrl.TrimEnd('/').ToLowerInvariant() + "|" + webUrl.ToLowerInvariant();
}