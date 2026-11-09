#nullable enable
using PnP.Scanning.Core.Pipeline.Collection.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;

namespace PnP.Scanning.Core.Pipeline.Orchestration;

/// <summary>
/// Binds a collector factory to an explicit input version and preflight validators.
/// </summary>
internal sealed record CollectionRegistration(
    string ModuleKey,
    string InputVersion,
    Func<ICollectionModule> Create,
    Action<VersionedJson>? ValidateParameters = null,
    Action<PnP.Scanning.Core.Services.StartRequest>? ValidateOptions = null);

/// <summary>
/// Binds an offline analyzer to a rule version, accepted inputs, and parameter pinning.
/// </summary>
internal sealed record AnalysisRegistration(
    string ModuleKey,
    string RuleVersion,
    IReadOnlyCollection<string> InputVersions,
    Func<IAnalysisModule> Create,
    Action<VersionedJson>? ValidateParameters = null,
    Func<VersionedJson, VersionedJson>? PinParameters = null);

/// <summary>
/// Resolves immutable module registrations without constructing authenticated services.
/// </summary>
internal sealed class ModuleRegistry
{
    private readonly Dictionary<string, CollectionRegistration> collectors;
    private readonly Dictionary<(string, string), AnalysisRegistration> analyzers;
    private readonly Dictionary<string, string> currentRules;

    /// <summary>
    /// Initializes Module Registry with the supplied dependencies and fixed scope.
    /// </summary>
    public ModuleRegistry(
        IEnumerable<CollectionRegistration>? collection = null,
        IEnumerable<AnalysisRegistration>? analysis = null,
        IReadOnlyDictionary<string, string>? current = null)
    {
        CollectionRegistration[]? collectionRows = (collection ?? []).ToArray();
        AnalysisRegistration[]? analysisRows =
            (analysis ?? []).Select(x => x with { InputVersions = x.InputVersions.ToArray() }).ToArray();
        if (collectionRows.Any(x =>
                string.IsNullOrWhiteSpace(x.ModuleKey) || string.IsNullOrWhiteSpace(x.InputVersion)) ||
            analysisRows.Any(x => string.IsNullOrWhiteSpace(x.ModuleKey) || string.IsNullOrWhiteSpace(x.RuleVersion) ||
                                  x.InputVersions.Count == 0 || x.InputVersions.Any(string.IsNullOrWhiteSpace)))
            throw new ArgumentException("Module, input and rule versions must be explicitly registered.");
        collectors = collectionRows.ToDictionary(x => x.ModuleKey, StringComparer.OrdinalIgnoreCase);
        analyzers = analysisRows.ToDictionary(x => (Normalize(x.ModuleKey), x.RuleVersion));
        currentRules = new Dictionary<string, string>(
            current ?? new Dictionary<string, string>(),
            StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> entry in currentRules) _ = GetAnalyzer(entry.Key, entry.Value);
    }

    /// <summary>
    /// Resolves a collector and rejects unavailable pinned input versions.
    /// </summary>
    public CollectionRegistration GetCollector(string key, string? inputVersion = null)
    {
        if (!collectors.TryGetValue(key, out CollectionRegistration? registration))
            throw new NotSupportedException(
                $"Module '{key}' does not support collect; its existing start path is legacy.");
        if (inputVersion != null && inputVersion != registration.InputVersion)
            throw new NotSupportedException(
                $"Collector for pinned input version '{inputVersion}' is unavailable for '{key}'.");
        return registration;
    }

    /// <summary>
    /// Resolves the requested rule and rejects incompatible pinned source versions.
    /// </summary>
    public AnalysisRegistration GetAnalyzer(string key, string? ruleVersion = null, string? inputVersion = null)
    {
        if (string.IsNullOrEmpty(ruleVersion) && !currentRules.TryGetValue(key, out ruleVersion))
            throw new NotSupportedException($"Module '{key}' does not support analyze; no current rule is registered.");
        if (!analyzers.TryGetValue((Normalize(key), ruleVersion!), out AnalysisRegistration? registration))
            throw new NotSupportedException($"Analysis rule '{ruleVersion}' is unavailable for module '{key}'.");
        if (inputVersion != null && !registration.InputVersions.Contains(inputVersion, StringComparer.Ordinal))
            throw new NotSupportedException(
                $"Analysis rule '{ruleVersion}' cannot consume input version '{inputVersion}'.");
        return registration;
    }

    /// <summary>
    /// Normalizes module keys for case-insensitive lookup without altering rule versions.
    /// </summary>
    private static string Normalize(string key) => key.ToLowerInvariant();
}