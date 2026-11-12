using PnP.Scanning.Core.ClassicPages.Contracts.Module;
using PnP.Scanning.Core.ClassicPages.Collection.Module;
using PnP.Scanning.Core.ClassicPages.Analysis.Module;
using PnP.Scanning.Core.Pipeline.Collection.Module;
#nullable enable
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Pipeline.Orchestration;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Services;

namespace PnP.Scanning.Core.ClassicPages;

/// <summary>
/// Registers versioned Classic object acquisition and Page analysis behind the existing Classic command.
/// </summary>
internal static class ClassicPageModule
{
    internal const string Key = ClassicContentContract.ModuleKey;
    internal const string InputVersion = ClassicContentContract.InputVersion;
    internal const string RuleVersion = ClassicContentContract.RuleVersion;

    /// <summary>
    /// Registers matching object contracts and rules; earlier snapshot shapes are rejected before execution rather than reinterpreted.
    /// </summary>
    internal static ModuleRegistry
        Registry(Func<CollectionContext, CancellationToken, Task<IClassicContentReader>>? online = null) =>
        new(
            [
                new CollectionRegistration(
                    Key,
                    InputVersion,
                    () => new ClassicContentCollectionModule(online),
                    ValidateParameters,
                    ValidateOptions)
            ],
            [
                new AnalysisRegistration(
                    Key,
                    RuleVersion,
                    [InputVersion],
                    () => new ClassicPageAnalysisModule(),
                    ClassicPageRuleMetadata.Validate,
                    ClassicPageRuleMetadata.Pin)
            ],
            new Dictionary<string, string> { [Key] = RuleVersion });

    /// <summary>
    /// Requires the typed-options collection parameter envelope without accepting unrecognized configuration.
    /// </summary>
    internal static void ValidateParameters(VersionedJson parameters)
    {
        if (parameters.SchemaVersion != 1 || parameters.Value.ValueKind != System.Text.Json.JsonValueKind.Object ||
            parameters.Value.EnumerateObject().Any())
            throw new ArgumentException(
                "Classic object collection uses typed Classic options; module parameters must be empty.");
    }

    /// <summary>
    /// Validates existing Pages selection, authentication metadata, property uniqueness, and audit bounds before acquisition.
    /// </summary>
    internal static void ValidateOptions(StartRequest request)
    {
        if (!Handles(request))
            throw new ArgumentException("The page pipeline requires Classic mode with the Pages component.");
        if (!Guid.TryParse(request.ApplicationId, out Guid application) || application == Guid.Empty)
            throw new ArgumentException("A nonempty application ID is required for Classic collection.");
        if (string.IsNullOrWhiteSpace(request.Tenant))
            throw new ArgumentException("A tenant is required for Classic collection.");
        if (!Enum.TryParse<Authentication.AuthenticationMode>(request.AuthMode, true, out _) ||
            !Enum.TryParse<global::PnP.Core.Services.Microsoft365Environment>(request.Environment, true, out _))
            throw new ArgumentException("A supported authentication mode and cloud environment are required.");
        if (request.Properties.GroupBy(property => property.Property, StringComparer.Ordinal)
            .Any(group => group.Count() > 1))
            throw new ArgumentException("Collection property names must be unique.");
        if (Options(request).AuditLogWindowDays is < 1 or > 180)
            throw new ArgumentException("Audit log window must be between 1 and 180 days.");
    }

    /// <summary>
    /// Reads fixed acquisition flags from persisted existing start properties.
    /// </summary>
    internal static ClassicCollectionOptions Options(StartRequest request)
    {
        bool Flag(string key) =>
            request.Properties.FirstOrDefault(property => property.Property == key)?.Value is string value &&
            bool.Parse(value);

        string? window = request.Properties
            .FirstOrDefault(property => property.Property == Constants.StartClassicAuditLogWindowDays)
            ?.Value;
        return new(
            Flag(Constants.StartClassicExportWebPartProperties),
            Flag(Constants.StartClassicSkipUsageInformation),
            Flag(Constants.StartClassicSkipUserInformation),
            Flag(Constants.StartClassicHomePageOnly),
            window == null ? 14 : int.Parse(window),
            request.Properties.FirstOrDefault(property => property.Property == Constants.StartExcludeSiteTemplates)
                ?.Value);
    }

    /// <summary>
    /// Selects the internal object pipeline for existing Classic requests that include Pages.
    /// </summary>
    internal static bool Handles(StartRequest request) =>
        request.Mode.Equals(Mode.Classic.ToString(), StringComparison.OrdinalIgnoreCase) &&
        OptionsBase.FromScannerInput(request) is ClassicOptions { Pages: true };

    /// <summary>
    /// Determines whether retained non-page collectors accompany Pages acquisition.
    /// </summary>
    internal static bool HasOtherComponents(StartRequest request) =>
        OptionsBase.FromScannerInput(request) is ClassicOptions options &&
        (options.Lists || options.InfoPath || options.Workflow || options.Extensibility);
}